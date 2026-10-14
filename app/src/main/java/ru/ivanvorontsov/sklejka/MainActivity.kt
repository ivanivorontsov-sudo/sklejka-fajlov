package ru.ivanvorontsov.sklejka

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.DocumentsContract
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import ru.ivanvorontsov.sklejka.core.*
import ru.ivanvorontsov.sklejka.service.Job
import ru.ivanvorontsov.sklejka.service.Work
import ru.ivanvorontsov.sklejka.ui.App
import ru.ivanvorontsov.sklejka.ui.SklejkaTheme
import ru.ivanvorontsov.sklejka.ui.ThemeMode

/** Everything the join screen knows about the currently loaded documents. */
class JoinState {
    var docs by mutableStateOf<List<Doc>>(emptyList())
    var groups by mutableStateOf<List<PartGroup>>(emptyList())
    var selected by mutableIntStateOf(0)
    var order by mutableStateOf<List<Part>>(emptyList())
    var singlePick by mutableStateOf<Uri?>(null)
    var loading by mutableStateOf(false)
    val group get() = groups.getOrNull(selected)
    val checksumDocs get() = docs.filter { ChecksumFiles.isChecksumFile(it.name) }
}

class MainActivity : ComponentActivity() {
    val join = JoinState()
    private val prefs by lazy { getSharedPreferences("settings", 0) }
    var themeMode by mutableStateOf(ThemeMode.SYSTEM)
    var dynamicColor by mutableStateOf(false)
    var allowDelete by mutableStateOf(false)
    var pendingIntent by mutableStateOf<Intent?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        themeMode = runCatching { ThemeMode.valueOf(prefs.getString("theme", "SYSTEM")!!) }.getOrDefault(ThemeMode.SYSTEM)
        dynamicColor = prefs.getBoolean("dynamic", false)
        allowDelete = prefs.getBoolean("allowDelete", false)
        if (savedInstanceState == null) pendingIntent = intent
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        setContent { SklejkaTheme(themeMode, dynamicColor) { App(this) } }
    }

    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); pendingIntent = intent }

    fun saveSettings() {
        prefs.edit().putString("theme", themeMode.name).putBoolean("dynamic", dynamicColor).putBoolean("allowDelete", allowDelete).apply()
    }

    /** Uris delivered via share / «Открыть с помощью». */
    fun urisFrom(i: Intent): List<Uri> = when (i.action) {
        Intent.ACTION_SEND -> listOfNotNull(if (Build.VERSION.SDK_INT >= 33) i.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java) else @Suppress("DEPRECATION") i.getParcelableExtra(Intent.EXTRA_STREAM))
        Intent.ACTION_SEND_MULTIPLE -> (if (Build.VERSION.SDK_INT >= 33) i.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java) else @Suppress("DEPRECATION") i.getParcelableArrayListExtra(Intent.EXTRA_STREAM)) ?: emptyList()
        Intent.ACTION_VIEW -> listOfNotNull(i.data)
        else -> emptyList()
    }

    suspend fun loadDocs(uris: List<Uri>, fromTree: Boolean = false, keepExisting: Boolean = false) {
        join.loading = true
        try {
            val docs = withContext(Dispatchers.IO) { uris.map { Saf.describe(this@MainActivity, it) } }
            setDocs(if (keepExisting) (join.docs + docs).distinctBy { it.uri } else docs)
            join.singlePick = if (!fromTree && uris.size == 1) uris.first() else join.singlePick.takeIf { keepExisting }
        } finally { join.loading = false }
    }

    suspend fun loadTree(tree: Uri) {
        join.loading = true
        try {
            val all = withContext(Dispatchers.IO) { runCatching { Saf.listTree(this@MainActivity, tree) }.getOrDefault(emptyList()) }
            val wanted = join.group?.outputName
            setDocs(all)
            if (wanted != null) join.groups.indexOfFirst { it.outputName.equals(wanted, true) }.takeIf { it >= 0 }?.let { selectGroup(it) }
            join.singlePick = null
            if (join.groups.isEmpty()) toast("В папке не нашлось разрезанных файлов")
        } finally { join.loading = false }
    }

    private fun setDocs(docs: List<Doc>) {
        join.docs = docs
        join.groups = PartDetector.group(docs.map { Triple(it.uri.toString(), it.name, it.size) })
        selectGroup(0)
    }

    fun selectGroup(i: Int) { join.selected = i; join.order = join.groups.getOrNull(i)?.parts ?: emptyList() }

    fun startJoin(out: Uri, name: String) {
        val parts = join.order
        val need = parts.sumOf { it.size }
        val free = Saf.freeSpace(this, out)
        if (free != null && free < need) {
            Saf.delete(this, out)
            toast("Не хватает места: нужно ${Fmt.size(need)}, свободно ${Fmt.size(free)}"); return
        }
        parts.forEach { Saf.persist(this, Uri.parse(it.id)) }
        Saf.persist(this, out, write = true)
        Work.start(this, Job.Join(parts.map { Uri.parse(it.id) }, parts.map { it.size }, out, name))
    }

    fun expectedChecksums(name: String): List<Pair<String, ChecksumFiles.Expect>> =
        join.checksumDocs.mapNotNull { d -> Saf.readText(this, d.uri)?.let { t -> ChecksumFiles.parse(d.name, t, name)?.let { d.name to it } } }

    fun open(uri: Uri, name: String) {
        val mime = contentResolver.getType(uri) ?: android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "*/*"
        val i = Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        runCatching { startActivity(Intent.createChooser(i, "Открыть $name")) }.onFailure { toast("Нет приложения, чтобы открыть этот файл") }
    }

    fun share(uri: Uri) {
        val i = Intent(Intent.ACTION_SEND).setType("*/*").putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        startActivity(Intent.createChooser(i, "Отправить"))
    }

    fun copy(label: String, text: String) {
        getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText(label, text)); toast("$label скопирован")
    }

    fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_LONG).show()

    /** Extract a standard zip into [tree]; returns number of files. */
    suspend fun extractZip(zip: Uri, tree: Uri, onFile: (String) -> Unit): Int = withContext(Dispatchers.IO) {
        val root = DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree))
        val dirs = HashMap<String, Uri>().apply { put("", root) }
        fun dir(path: String): Uri {
            dirs[path]?.let { return it }
            val parent = dir(path.substringBeforeLast('/', ""))
            val u = DocumentsContract.createDocument(contentResolver, parent, DocumentsContract.Document.MIME_TYPE_DIR, path.substringAfterLast('/'))!!
            dirs[path] = u; return u
        }
        var n = 0
        java.util.zip.ZipInputStream(contentResolver.openInputStream(zip)!!.buffered(1 shl 16)).use { z ->
            val buf = ByteArray(1 shl 16)
            while (true) {
                val e = z.nextEntry ?: break
                val path = e.name.replace('\\', '/').split('/').filter { it.isNotEmpty() && it != "." && it != ".." }.joinToString("/")
                if (path.isEmpty()) continue
                if (e.isDirectory) { dir(path); continue }
                onFile(path)
                val u = DocumentsContract.createDocument(contentResolver, dir(path.substringBeforeLast('/', "")), "application/octet-stream", path.substringAfterLast('/'))!!
                contentResolver.openOutputStream(u, "wt")!!.use { o -> while (true) { val r = z.read(buf); if (r < 0) break; o.write(buf, 0, r) } }
                n++
            }
        }
        n
    }
}


