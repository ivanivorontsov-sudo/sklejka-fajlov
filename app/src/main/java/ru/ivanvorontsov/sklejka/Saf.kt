package ru.ivanvorontsov.sklejka

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import android.system.Os

data class Doc(val uri: Uri, val name: String, val size: Long)

object Saf {
    fun describe(ctx: Context, uri: Uri): Doc {
        var name = uri.lastPathSegment?.substringAfterLast('/') ?: "file"
        var size = -1L
        runCatching {
            ctx.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                if (c.moveToFirst()) {
                    c.getString(0)?.let { name = it }
                    if (!c.isNull(1)) size = c.getLong(1)
                }
            }
        }
        if (size < 0) size = runCatching { ctx.contentResolver.openFileDescriptor(uri, "r")?.use { it.statSize } }.getOrNull() ?: -1
        if (uri.scheme == "file") uri.path?.let { java.io.File(it).let { f -> name = f.name; size = f.length() } }
        return Doc(uri, name, size)
    }

    fun persist(ctx: Context, uri: Uri, write: Boolean = false) {
        val flags = Intent.FLAG_GRANT_READ_URI_PERMISSION or (if (write) Intent.FLAG_GRANT_WRITE_URI_PERMISSION else 0)
        runCatching { ctx.contentResolver.takePersistableUriPermission(uri, flags) }
    }

    /** Children of a tree URI (non-recursive). */
    fun listTree(ctx: Context, tree: Uri): List<Doc> {
        val parentId = DocumentsContract.getTreeDocumentId(tree)
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, parentId)
        val out = ArrayList<Doc>()
        ctx.contentResolver.query(children, arrayOf(
            DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_SIZE, DocumentsContract.Document.COLUMN_MIME_TYPE), null, null, null)?.use { c ->
            while (c.moveToNext()) {
                if (c.getString(3) == DocumentsContract.Document.MIME_TYPE_DIR) continue
                out += Doc(DocumentsContract.buildDocumentUriUsingTree(tree, c.getString(0)), c.getString(1) ?: continue, c.getLong(2))
            }
        }
        return out
    }

    /** Folder that contains [doc] when it is an ExternalStorage document, to pre-open the folder picker there. */
    fun parentHint(doc: Uri): Uri? = runCatching {
        if (doc.authority != "com.android.externalstorage.documents") return null
        val id = DocumentsContract.getDocumentId(doc)
        val parent = id.substringBeforeLast('/', id.substringBefore(':') + ":")
        DocumentsContract.buildDocumentUri(doc.authority, parent)
    }.getOrNull()

    fun createIn(ctx: Context, tree: Uri, name: String, mime: String = "application/octet-stream"): Uri? {
        val dir = DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree))
        return DocumentsContract.createDocument(ctx.contentResolver, dir, mime, name)
    }

    /** Free bytes on the volume behind [uri], or null if unknown. */
    fun freeSpace(ctx: Context, uri: Uri): Long? = runCatching {
        ctx.contentResolver.openFileDescriptor(uri, "r")?.use { pfd ->
            val st = Os.fstatvfs(pfd.fileDescriptor); st.f_bavail * st.f_frsize
        }
    }.getOrNull()

    fun delete(ctx: Context, uri: Uri): Boolean =
        runCatching { DocumentsContract.deleteDocument(ctx.contentResolver, uri) }.getOrDefault(false)

    fun readText(ctx: Context, uri: Uri, max: Int = 1 shl 20): String? = runCatching {
        ctx.contentResolver.openInputStream(uri)?.use { s -> String(s.readNBytesCompat(max), Charsets.UTF_8) }
    }.getOrNull()

    private fun java.io.InputStream.readNBytesCompat(max: Int): ByteArray {
        val bos = java.io.ByteArrayOutputStream(); val b = ByteArray(8192); var t = 0
        while (t < max) { val n = read(b, 0, minOf(b.size, max - t)); if (n < 0) break; bos.write(b, 0, n); t += n }
        return bos.toByteArray()
    }
}
