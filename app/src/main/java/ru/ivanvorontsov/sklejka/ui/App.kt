package ru.ivanvorontsov.sklejka.ui

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.*
import androidx.compose.animation.core.*
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.zIndex
import kotlinx.coroutines.launch
import ru.ivanvorontsov.sklejka.MainActivity
import ru.ivanvorontsov.sklejka.Saf
import ru.ivanvorontsov.sklejka.core.*
import ru.ivanvorontsov.sklejka.service.Job
import ru.ivanvorontsov.sklejka.service.JobResult
import ru.ivanvorontsov.sklejka.service.Work

@Composable
fun App(a: MainActivity) {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    var settings by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val ui by Work.ui.collectAsState()

    LaunchedEffect(a.pendingIntent) {
        val i = a.pendingIntent ?: return@LaunchedEffect
        a.pendingIntent = null
        val uris = a.urisFrom(i)
        if (uris.isNotEmpty()) { tab = 0; a.loadDocs(uris) }
    }

    Scaffold(
        topBar = {
            LargeTopAppBar(
                title = { Text("Склейка файлов", fontWeight = FontWeight.SemiBold) },
                actions = { IconButton({ settings = true }) { Icon(Icons.Default.Settings, "Настройки") } },
            )
        },
        bottomBar = {
            NavigationBar {
                NavigationBarItem(selected = tab == 0, onClick = { tab = 0 }, icon = { Icon(Icons.Default.Add, null) }, label = { Text("Соединить") })
                NavigationBarItem(selected = tab == 1, onClick = { tab = 1 }, icon = { Icon(Icons.Default.Share, null) }, label = { Text("Разрезать") })
            }
        },
    ) { pad ->
        Box(Modifier.padding(pad).fillMaxSize()) {
            Crossfade(tab, animationSpec = tween(220), label = "tab") { t ->
                if (t == 0) JoinScreen(a, ui.running) else SplitScreen(a, ui.running)
            }
        }
    }

    if (ui.running) ProgressSheet(ui)
    ui.result?.let { ResultDialog(a, it) }
    if (settings) SettingsDialog(a) { settings = false }
}

@Composable
private fun Section(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) =
    ElevatedCard(modifier.fillMaxWidth(), shape = RoundedCornerShape(24.dp)) { Column(Modifier.padding(18.dp), content = content) }

@Composable
private fun JoinScreen(a: MainActivity, busy: Boolean) {
    val s = a.join
    val scope = rememberCoroutineScope()
    val pickParts = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { l -> if (l.isNotEmpty()) scope.launch { a.loadDocs(l) } }
    val pickMore = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { l -> if (l.isNotEmpty()) scope.launch { a.loadDocs(l, keepExisting = true) } }
    val pickTree = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { t -> if (t != null) { Saf.persist(a, t); scope.launch { a.loadTree(t) } } }
    val group = s.group
    val create = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { out ->
        if (out != null && group != null) a.startJoin(out, Saf.describe(a, out).name)
    }
    val resume = remember { mutableStateOf(Work.checkpoint(a)) }
    LaunchedEffect(busy) { resume.value = if (busy) null else Work.checkpoint(a) }

    LazyColumn(contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        resume.value?.let { r ->
            item {
                Section {
                    Text("Прерванное соединение", style = MaterialTheme.typography.titleMedium)
                    Text("${r.outputName}: записано ${Fmt.size(r.resumeFrom)} из ${Fmt.size(r.sizes.sum())}", style = MaterialTheme.typography.bodyMedium)
                    Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Button({ Work.start(a, r) }, enabled = !busy) { Text("Продолжить") }
                        TextButton({ Work.clearCheckpoint(a); resume.value = null }) { Text("Забыть") }
                    }
                }
            }
        }
        item {
            Section {
                Text("Выберите части", style = MaterialTheme.typography.titleMedium)
                Text("Можно выбрать все части сразу, одну первую (остальные найдутся в папке) или целую папку.",
                    style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(12.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button({ pickParts.launch(arrayOf("*/*")) }, Modifier.weight(1f)) { Text("Файлы") }
                    FilledTonalButton({ pickTree.launch(null) }, Modifier.weight(1f)) { Text("Папка") }
                }
                if (s.loading) LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 12.dp))
            }
        }
        if (s.docs.isNotEmpty() && s.groups.isEmpty()) item {
            Section { Text("Это не похоже на части разрезанного файла.", style = MaterialTheme.typography.bodyLarge)
                Text("Поддерживаются: .001/.002, .part1, файл.ext.1, файл_1.ext, .gsd, .aa/.ab, .7z.001, .zip.001, .z01, .part1.rar", style = MaterialTheme.typography.bodySmall) }
        }
        if (s.groups.size > 1) item {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                s.groups.forEachIndexed { i, g -> FilterChip(i == s.selected, { a.selectGroup(i) }, { Text(g.outputName, maxLines = 1, overflow = TextOverflow.Ellipsis) }) }
            }
        }
        if (group != null) {
            val ordered = PartGroup(group.outputName, group.kind, s.order)
            item { GroupSummary(ordered, s.singlePick != null || ordered.missing.isNotEmpty(),
                onFind = { pickTree.launch(s.singlePick?.let { Saf.parentHint(it) }) }, onAdd = { pickMore.launch(arrayOf("*/*")) }) }
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Порядок частей", style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
                    TextButton({ s.order = PartDetector.sortParts(s.order) }) { Text("По номерам") }
                }
                Text("Удерживайте часть, чтобы перетащить", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            item { ReorderList(s.order) { s.order = it } }
            item {
                val enabled = !busy && group.kind.joinable && s.order.isNotEmpty()
                Button({ create.launch(group.outputName) }, Modifier.fillMaxWidth().height(56.dp), enabled = enabled, shape = RoundedCornerShape(18.dp)) {
                    Text(if (group.kind.joinable) "Соединить → ${group.outputName}" else "Эти тома не склеивают", maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                if (!group.kind.joinable) OutlinedButton({ s.order.firstOrNull()?.let { a.open(Uri.parse(it.id), it.name) } }, Modifier.fillMaxWidth().padding(top = 8.dp)) {
                    Text("Открыть первый том в архиваторе")
                }
            }
        }
        item { Spacer(Modifier.height(24.dp)) }
    }
}

@Composable
private fun GroupSummary(g: PartGroup, offerFind: Boolean, onFind: () -> Unit, onAdd: () -> Unit) {
    Section {
        Text(g.outputName, style = MaterialTheme.typography.titleLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
        Text(g.kind.title, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.height(8.dp))
        Text("${g.parts.size} ${plural(g.parts.size)} · ${Fmt.size(g.totalSize)}", style = MaterialTheme.typography.bodyLarge)
        val warn = buildList {
            if (g.missing.isNotEmpty()) add("Не хватает частей: " + g.missing.joinToString(", ") { "№$it" })
            if (g.duplicates.isNotEmpty()) add("Лишние повторы: " + g.duplicates.joinToString(", ") { "№$it" })
            g.oddSized.takeIf { it.isNotEmpty() }?.let { add("Размер отличается у: " + it.joinToString(", ") { p -> p.name }) }
            if (g.kind == ru.ivanvorontsov.sklejka.core.PartKind.RAR_VOLUMES)
                add("Это тома RAR: у каждого свой заголовок, простое соединение даст битый архив. Откройте первый том в архиваторе (ZArchiver, RAR) — он сам прочтёт остальные.")
            if (g.kind == ru.ivanvorontsov.sklejka.core.PartKind.ZIP_SPAN)
                add("Это многотомный zip (.z01 … .zip): склеивать его нельзя, откройте .zip в архиваторе, положив тома рядом.")
            if (g.kind == ru.ivanvorontsov.sklejka.core.PartKind.GSPLIT)
                add("GSplit по умолчанию добавляет в части свой заголовок. Склейка верна, только если части сделаны без заголовка.")
        }
        AnimatedVisibility(warn.isNotEmpty()) {
            Column(Modifier.padding(top = 10.dp).clip(RoundedCornerShape(16.dp)).background(MaterialTheme.colorScheme.errorContainer).padding(12.dp)) {
                warn.forEach { Text("• $it", color = MaterialTheme.colorScheme.onErrorContainer, style = MaterialTheme.typography.bodyMedium) }
            }
        }
        if (offerFind) Row(Modifier.padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilledTonalButton(onFind) { Text("Найти в папке") }
            OutlinedButton(onAdd) { Text("Добавить файлы") }
        }
    }
}

private fun plural(n: Int) = when { n % 10 == 1 && n % 100 != 11 -> "часть"; n % 10 in 2..4 && n % 100 !in 12..14 -> "части"; else -> "частей" }

@Composable
private fun ReorderList(items: List<Part>, onChange: (List<Part>) -> Unit) {
    var dragIndex by remember { mutableIntStateOf(-1) }
    var dragOffset by remember { mutableFloatStateOf(0f) }
    var itemH by remember { mutableFloatStateOf(1f) }
    val current by rememberUpdatedState(items)
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        items.forEachIndexed { i, p ->
            val dragging = i == dragIndex
            val elev by animateDpAsState(if (dragging) 8.dp else 0.dp, label = "e")
            key(p.id) {
                Surface(
                    shape = RoundedCornerShape(16.dp), tonalElevation = 1.dp, shadowElevation = elev,
                    color = if (dragging) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainer,
                    modifier = Modifier.fillMaxWidth().zIndex(if (dragging) 1f else 0f)
                        .graphicsLayer { translationY = if (dragging) dragOffset else 0f; itemH = size.height + 6.dp.toPx() }
                        .pointerInput(p.id) {
                            detectDragGesturesAfterLongPress(
                                onDragStart = { dragIndex = current.indexOfFirst { it.id == p.id }; dragOffset = 0f },
                                onDragEnd = { dragIndex = -1; dragOffset = 0f }, onDragCancel = { dragIndex = -1; dragOffset = 0f },
                                onDrag = { ch, d ->
                                    ch.consume(); dragOffset += d.y
                                    val l = current; val from = dragIndex
                                    val to = (from + (dragOffset / itemH).toInt()).coerceIn(0, l.lastIndex)
                                    if (from >= 0 && to != from) {
                                        val m = l.toMutableList(); m.add(to, m.removeAt(from)); onChange(m)
                                        dragOffset -= (to - from) * itemH; dragIndex = to
                                    }
                                })
                        },
                ) {
                    Row(Modifier.padding(horizontal = 14.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(32.dp).clip(CircleShape).background(MaterialTheme.colorScheme.primary), contentAlignment = Alignment.Center) {
                            Text("${i + 1}", color = MaterialTheme.colorScheme.onPrimary, style = MaterialTheme.typography.labelLarge)
                        }
                        Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                            Text(p.name, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
                            Text(Fmt.size(p.size), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        Icon(Icons.Default.Menu, "Перетащить", tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
    }
}

@Composable
private fun SplitScreen(a: MainActivity, busy: Boolean) {
    var file by remember { mutableStateOf<ru.ivanvorontsov.sklejka.Doc?>(null) }
    val presets = listOf("10 МБ" to (10L shl 20), "100 МБ" to (100L shl 20), "700 МБ (CD)" to (700L shl 20), "1 ГБ" to (1L shl 30), "2 ГБ" to (2L shl 30), "4 ГБ − 1 (FAT32)" to ((4L shl 30) - 1))
    var preset by rememberSaveable { mutableIntStateOf(1) }
    var custom by rememberSaveable { mutableStateOf("") }
    var writeCrc by rememberSaveable { mutableStateOf(true) }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { u -> if (u != null) { Saf.persist(a, u); file = Saf.describe(a, u) } }
    val partSize = if (preset == -1) ((custom.replace(',', '.').toDoubleOrNull() ?: 0.0) * (1 shl 20)).toLong() else presets[preset].second
    val folder = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { t ->
        val f = file
        if (t != null && f != null) {
            Saf.persist(a, t, write = true)
            val tmp = Saf.createIn(a, t, ".sklejka-check")
            val free = tmp?.let { Saf.freeSpace(a, it) }; tmp?.let { Saf.delete(a, it) }
            if (free != null && free < f.size) a.toast("Не хватает места: нужно ${Fmt.size(f.size)}, свободно ${Fmt.size(free)}")
            else Work.start(a, Job.Split(f.uri, f.name, f.size, partSize, t, writeCrc))
        }
    }
    LazyColumn(contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item {
            Section {
                Text("Файл", style = MaterialTheme.typography.titleMedium)
                file?.let { Text("${it.name} · ${Fmt.size(it.size)}", style = MaterialTheme.typography.bodyLarge, modifier = Modifier.padding(vertical = 6.dp)) }
                Button({ pick.launch(arrayOf("*/*")) }) { Text(if (file == null) "Выбрать файл" else "Другой файл") }
            }
        }
        item {
            Section {
                Text("Размер части", style = MaterialTheme.typography.titleMedium)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    presets.forEachIndexed { i, (t, _) -> FilterChip(preset == i, { preset = i }, { Text(t) }) }
                    FilterChip(preset == -1, { preset = -1 }, { Text("Свой") })
                }
                AnimatedVisibility(preset == -1) {
                    OutlinedTextField(custom, { custom = it }, label = { Text("Размер в МБ") }, singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal), modifier = Modifier.fillMaxWidth())
                }
                file?.let { f -> if (partSize > 0) Text("Получится ${Streamer.partCount(f.size, partSize)} ${plural(Streamer.partCount(f.size, partSize))}: ${f.name}.001 …",
                    style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 8.dp)) }
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 8.dp).clickable { writeCrc = !writeCrc }) {
                    Checkbox(writeCrc, { writeCrc = it }); Text("Создать .crc для проверки (как Total Commander)")
                }
            }
        }
        item {
            Button({ folder.launch(null) }, Modifier.fillMaxWidth().height(56.dp), enabled = !busy && file != null && partSize > 0, shape = RoundedCornerShape(18.dp)) {
                Text("Выбрать папку и разрезать")
            }
        }
    }
}

@Composable
private fun ProgressSheet(ui: ru.ivanvorontsov.sklejka.service.JobUi) {
    val frac = if (ui.total > 0) ui.done.toFloat() / ui.total else 0f
    val anim by animateFloatAsState(frac, tween(120, easing = LinearEasing), label = "p")
    AlertDialog(onDismissRequest = {}, confirmButton = {}, dismissButton = { TextButton({ Work.cancel = true }) { Text("Отмена") } },
        title = { Text(ui.title, maxLines = 2, overflow = TextOverflow.Ellipsis) },
        text = {
            Column {
                LinearProgressIndicator({ anim }, Modifier.fillMaxWidth().height(10.dp).clip(RoundedCornerShape(5.dp)))
                Spacer(Modifier.height(10.dp))
                Text("${Fmt.size(ui.done)} из ${Fmt.size(ui.total)} · ${(frac * 100).toInt()}%")
                if (ui.partCount > 0) Text("Часть ${ui.partIndex} из ${ui.partCount} · ${Fmt.size(ui.speed)}/с")
                val eta = if (ui.speed > 0) (ui.total - ui.done) / ui.speed else -1
                if (eta >= 0) Text("Осталось ≈ ${eta / 60} мин ${eta % 60} с", color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text("Можно свернуть приложение — работа продолжится, прогресс в уведомлении.", style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 8.dp))
            }
        })
}

@Composable
private fun ResultDialog(a: MainActivity, r: JobResult) {
    val scope = rememberCoroutineScope()
    var verify by remember { mutableStateOf<List<Pair<String, List<Pair<String, Boolean>>>>?>(null) }
    var confirmDelete by remember { mutableStateOf(false) }
    var extracting by remember { mutableStateOf<String?>(null) }
    val job = r.job
    LaunchedEffect(r) {
        if (r.ok && job is Job.Join && r.hashes != null) verify = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            a.expectedChecksums(r.outputName).map { (n, e) -> n to ChecksumFiles.verify(e, r.hashes, r.size) }
        }
    }
    val extractTo = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { t ->
        val out = r.output
        if (t != null && out != null) scope.launch {
            extracting = "…"
            runCatching { a.extractZip(out, t) { extracting = it } }
                .onSuccess { a.toast("Распаковано файлов: $it") }.onFailure { a.toast("Не удалось распаковать: ${it.message}") }
            extracting = null
        }
    }
    AlertDialog(
        onDismissRequest = { Work.clearResult() },
        icon = { Icon(if (r.ok) Icons.Default.CheckCircle else Icons.Default.Warning, null) },
        title = { Text(if (r.ok) "Готово" else "Не получилось") },
        text = {
            Column(Modifier.heightIn(max = 460.dp).verticalScrollCompat()) {
                Text(r.message)
                r.hashes?.let { h ->
                    Spacer(Modifier.height(10.dp))
                    HashRow(a, "CRC32", h.crc32); HashRow(a, "MD5", h.md5); HashRow(a, "SHA-256", h.sha256)
                }
                verify?.forEach { (file, checks) ->
                    val ok = checks.isNotEmpty() && checks.all { it.second }
                    Text((if (ok) "✓ Совпадает с $file" else "✗ Не совпадает с $file: ") + checks.filter { !it.second }.joinToString { it.first },
                        color = if (ok) MaterialTheme.colorScheme.tertiary else MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 8.dp))
                }
                extracting?.let { Text("Распаковка: $it", modifier = Modifier.padding(top = 8.dp)); LinearProgressIndicator(Modifier.fillMaxWidth()) }
                if (r.ok && job is Job.Join) {
                    FlowRow(Modifier.padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilledTonalButton({ a.open(r.output!!, r.outputName) }) { Text("Открыть") }
                        if (r.outputName.endsWith(".zip", true)) FilledTonalButton({ extractTo.launch(null) }, enabled = extracting == null) { Text("Распаковать") }
                        OutlinedButton({ a.share(r.output!!) }) { Text("Отправить") }
                        if (a.allowDelete) OutlinedButton({ confirmDelete = true }) { Text("Удалить части") }
                    }
                    if (r.outputName.endsWith(".7z", true) || r.outputName.endsWith(".rar", true))
                        Text("Архив .7z/.rar откройте кнопкой «Открыть» в ZArchiver или RAR.", style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 6.dp))
                }
                if (!r.ok && job is Job.Join && Work.checkpoint(a) != null)
                    Button({ Work.clearResult(); Work.checkpoint(a)?.let { Work.start(a, it) } }, Modifier.padding(top = 10.dp)) { Text("Продолжить с места остановки") }
            }
        },
        confirmButton = { TextButton({ Work.clearResult() }) { Text("Закрыть") } },
    )
    if (confirmDelete && job is Job.Join) AlertDialog(
        onDismissRequest = { confirmDelete = false },
        title = { Text("Удалить ${job.parts.size} ${plural(job.parts.size)}?") },
        text = { Text("Исходные части будут удалены без корзины. Результат ${r.outputName} останется.") },
        confirmButton = { TextButton({
            confirmDelete = false
            val okCount = job.parts.count { Saf.delete(a, it) }
            a.toast(if (okCount == job.parts.size) "Части удалены" else "Удалено $okCount из ${job.parts.size}: остальные приложение-хранилище удалить не дало")
        }) { Text("Удалить", color = MaterialTheme.colorScheme.error) } },
        dismissButton = { TextButton({ confirmDelete = false }) { Text("Отмена") } },
    )
}

@Composable
private fun Modifier.verticalScrollCompat(): Modifier = this.then(Modifier.verticalScroll(androidx.compose.foundation.rememberScrollState()))

@Composable
private fun HashRow(a: MainActivity, label: String, v: String) {
    Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).clickable { a.copy(label, v) }.padding(vertical = 4.dp)) {
        Text(label, Modifier.width(72.dp), style = MaterialTheme.typography.labelLarge)
        Text(v, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall)
    }
}

@Composable
private fun SettingsDialog(a: MainActivity, onClose: () -> Unit) {
    AlertDialog(onDismissRequest = { a.saveSettings(); onClose() }, title = { Text("Настройки") },
        text = {
            Column {
                Text("Тема", style = MaterialTheme.typography.titleSmall)
                ThemeMode.entries.forEach { m ->
                    Row(Modifier.fillMaxWidth().clickable { a.themeMode = m }, verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(a.themeMode == m, { a.themeMode = m }); Text(m.title)
                    }
                }
                if (android.os.Build.VERSION.SDK_INT >= 31) Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Цвета обоев", Modifier.weight(1f)); Switch(a.dynamicColor, { a.dynamicColor = it })
                }
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 8.dp)) {
                    Column(Modifier.weight(1f)) {
                        Text("Удалять части после соединения")
                        Text("Появится кнопка, удаление всегда с подтверждением", style = MaterialTheme.typography.bodySmall)
                    }
                    Switch(a.allowDelete, { a.allowDelete = it })
                }
            }
        },
        confirmButton = { TextButton({ a.saveSettings(); onClose() }) { Text("Готово") } })
}
