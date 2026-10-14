package ru.ivanvorontsov.sklejka.service

import android.app.*
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.os.SystemClock
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import ru.ivanvorontsov.sklejka.MainActivity
import ru.ivanvorontsov.sklejka.R
import ru.ivanvorontsov.sklejka.Saf
import ru.ivanvorontsov.sklejka.core.*
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.OutputStream

sealed interface Job {
    data class Join(val parts: List<Uri>, val sizes: List<Long>, val output: Uri, val outputName: String, val resumeFrom: Long = 0) : Job
    data class Split(val input: Uri, val inputName: String, val size: Long, val partSize: Long, val folder: Uri, val writeCrc: Boolean) : Job
}

data class JobUi(
    val running: Boolean = false,
    val title: String = "",
    val done: Long = 0, val total: Long = 0, val partIndex: Int = 0, val partCount: Int = 0,
    val speed: Long = 0,
    val result: JobResult? = null,
)

data class JobResult(
    val ok: Boolean, val message: String, val hashes: Hashes? = null, val size: Long = 0,
    val output: Uri? = null, val outputName: String = "", val created: List<Uri> = emptyList(), val job: Job? = null,
)

/** In-process bus between UI and service. */
object Work {
    private val _ui = MutableStateFlow(JobUi()); val ui = _ui.asStateFlow()
    @Volatile var cancel = false
    @Volatile internal var pending: Job? = null
    internal fun set(v: JobUi) { _ui.value = v }
    fun clearResult() { _ui.value = _ui.value.copy(result = null) }

    fun start(ctx: Context, job: Job) {
        pending = job; cancel = false
        val i = Intent(ctx, WorkService::class.java)
        if (Build.VERSION.SDK_INT >= 26) ctx.startForegroundService(i) else ctx.startService(i)
    }

    // ---- resume checkpoint ----
    private const val P = "resume"
    fun saveCheckpoint(ctx: Context, j: Job.Join, written: Long) {
        val o = JSONObject().put("out", j.output.toString()).put("name", j.outputName).put("written", written)
            .put("parts", JSONArray(j.parts.map { it.toString() })).put("sizes", JSONArray(j.sizes))
        ctx.getSharedPreferences(P, 0).edit().putString("job", o.toString()).apply()
    }
    fun clearCheckpoint(ctx: Context) = ctx.getSharedPreferences(P, 0).edit().remove("job").apply()
    fun checkpoint(ctx: Context): Job.Join? = runCatching {
        val o = JSONObject(ctx.getSharedPreferences(P, 0).getString("job", null) ?: return null)
        val p = o.getJSONArray("parts"); val s = o.getJSONArray("sizes")
        Job.Join((0 until p.length()).map { Uri.parse(p.getString(it)) }, (0 until s.length()).map { s.getLong(it) },
            Uri.parse(o.getString("out")), o.getString("name"), o.getLong("written"))
    }.getOrNull()
}

class WorkService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private lateinit var nm: NotificationManager
    private var lastNotif = 0L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        nm = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(NotificationChannel(CH, "Соединение и разрезание", NotificationManager.IMPORTANCE_LOW))
            nm.createNotificationChannel(NotificationChannel(CH_DONE, "Готово", NotificationManager.IMPORTANCE_DEFAULT))
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_CANCEL) { Work.cancel = true; return START_NOT_STICKY }
        val job = Work.pending ?: run { stopSelf(); return START_NOT_STICKY }
        Work.pending = null
        val n = progressNotif("Подготовка…", 0, 0)
        if (Build.VERSION.SDK_INT >= 29) startForeground(ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC) else startForeground(ID, n)
        scope.launch { run(job) }
        return START_NOT_STICKY
    }

    private fun run(job: Job) {
        val wl = (getSystemService(POWER_SERVICE) as PowerManager).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "sklejka:work")
        wl.acquire(6 * 60 * 60 * 1000L)
        val result = try {
            when (job) { is Job.Join -> join(job); is Job.Split -> split(job) }
        } catch (e: Cancelled) {
            JobResult(false, "Отменено. " + if (job is Job.Join) "Можно продолжить с места остановки." else "", job = job)
        } catch (e: Throwable) {
            JobResult(false, "Ошибка: ${e.message ?: e.javaClass.simpleName}", job = job)
        } finally { if (wl.isHeld) wl.release() }
        Work.set(Work.ui.value.copy(running = false, result = result))
        stopForeground(STOP_FOREGROUND_REMOVE)
        nm.notify(ID_DONE, Notification.Builder(this, CH_DONE).setSmallIcon(R.drawable.ic_notif)
            .setContentTitle(if (result.ok) "Готово" else "Не завершено").setContentText(result.message)
            .setContentIntent(openApp()).setAutoCancel(true).build())
        stopSelf()
    }

    private fun tracker(title: String, count: Int): Progress {
        val t0 = SystemClock.elapsedRealtime(); var lastUi = 0L
        Work.set(JobUi(true, title, 0, 0, 0, count))
        return Progress { done, total, idx ->
            val now = SystemClock.elapsedRealtime()
            if (now - lastUi >= 66 || done == total) {
                lastUi = now
                val sp = if (now > t0) done * 1000 / (now - t0) else 0
                Work.set(JobUi(true, title, done, total, idx.coerceAtLeast(0) + 1, count, sp))
            }
            if (now - lastNotif >= 1000) { lastNotif = now; nm.notify(ID, progressNotif(title, done, total)) }
        }
    }

    private fun join(j: Job.Join): JobResult {
        val cr = contentResolver
        val total = j.sizes.sum()
        val pfd = cr.openFileDescriptor(j.output, if (j.resumeFrom > 0) "rw" else "rwt") ?: error("Не удалось открыть файл для записи")
        pfd.use {
            val fos = FileOutputStream(pfd.fileDescriptor)
            val ch = fos.channel
            if (j.resumeFrom > 0) { ch.truncate(j.resumeFrom); ch.position(j.resumeFrom) }
            var sinceSync = 0L
            val cp = object : OutputStream() {
                override fun write(b: Int) = fos.write(b)
                override fun write(b: ByteArray, off: Int, len: Int) {
                    fos.write(b, off, len); sinceSync += len
                    if (sinceSync >= CHECKPOINT) { runCatching { pfd.fileDescriptor.sync() }; sinceSync = 0; Work.saveCheckpoint(this@WorkService, j, ch.position()) }
                }
            }
            Work.saveCheckpoint(this, j, j.resumeFrom)
            val h = Streamer.join(
                inputs = j.parts.map { u -> { cr.openInputStream(u) ?: error("Нет доступа к части") } },
                sizes = j.sizes, out = cp, cancelled = { Work.cancel },
                progress = tracker(if (j.resumeFrom > 0) "Продолжаю: ${j.outputName}" else "Соединяю: ${j.outputName}", j.parts.size),
                skip = j.resumeFrom,
                already = { android.os.ParcelFileDescriptor.AutoCloseInputStream(cr.openFileDescriptor(j.output, "r")!!) },
            )
            runCatching { pfd.fileDescriptor.sync() }
            val written = ch.size()
            Work.clearCheckpoint(this)
            if (written != total) return JobResult(false, "Размер результата ${Fmt.size(written)} не совпадает с суммой частей ${Fmt.size(total)}", h, written, j.output, j.outputName, job = j)
            return JobResult(true, "${j.outputName} — ${Fmt.size(written)}", h, written, j.output, j.outputName, job = j)
        }
    }

    private fun split(s: Job.Split): JobResult {
        val created = ArrayList<Uri>()
        val count = Streamer.partCount(s.size, s.partSize)
        val digits = maxOf(3, count.toString().length)
        val input = contentResolver.openInputStream(s.input) ?: error("Нет доступа к файлу")
        val (n, h) = try {
            Streamer.split(input, s.size, s.partSize, { i ->
                val name = s.inputName + "." + i.toString().padStart(digits, '0')
                val u = Saf.createIn(this, s.folder, name) ?: error("Не удалось создать $name")
                created += u
                contentResolver.openOutputStream(u, "wt") ?: error("Нет доступа к $name")
            }, { Work.cancel }, tracker("Разрезаю: ${s.inputName}", count))
        } catch (e: Throwable) {
            created.forEach { Saf.delete(this, it) }; throw e
        }
        if (s.writeCrc) {
            val crcName = s.inputName.substringBeforeLast('.', s.inputName) + ".crc"
            Saf.createIn(this, s.folder, crcName, "text/plain")?.let { u ->
                contentResolver.openOutputStream(u, "wt")?.use { it.write(ChecksumFiles.tcCrc(s.inputName, s.size, h.crc32).toByteArray()) }
            }
        }
        return JobResult(true, "Готово: $n частей по ${Fmt.size(s.partSize)}", h, s.size, created = created, outputName = s.inputName, job = s)
    }

    private fun openApp() = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    private fun progressNotif(title: String, done: Long, total: Long): Notification {
        val cancel = PendingIntent.getService(this, 1, Intent(this, WorkService::class.java).setAction(ACTION_CANCEL), PendingIntent.FLAG_IMMUTABLE)
        val pct = if (total > 0) (done * 1000 / total).toInt() else 0
        return Notification.Builder(this, CH).setSmallIcon(R.drawable.ic_notif).setContentTitle(title)
            .setContentText(if (total > 0) "${Fmt.size(done)} из ${Fmt.size(total)} · ${pct / 10}%" else "")
            .setProgress(1000, pct, total <= 0).setOngoing(true).setOnlyAlertOnce(true).setContentIntent(openApp())
            .addAction(Notification.Action.Builder(null, "Отмена", cancel).build()).build()
    }

    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    companion object {
        const val CH = "work"; const val CH_DONE = "done"; const val ID = 1; const val ID_DONE = 2
        const val ACTION_CANCEL = "cancel"
        const val CHECKPOINT = 64L shl 20
    }
}
