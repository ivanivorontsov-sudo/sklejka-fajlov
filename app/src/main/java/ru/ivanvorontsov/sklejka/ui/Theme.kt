package ru.ivanvorontsov.sklejka.ui

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext

enum class ThemeMode(val title: String) { SYSTEM("Как в системе"), LIGHT("Светлая"), DARK("Тёмная") }

private val Light = lightColorScheme(
    primary = Color(0xFF6750A4), onPrimary = Color.White, primaryContainer = Color(0xFFE9DDFF), onPrimaryContainer = Color(0xFF22005D),
    secondaryContainer = Color(0xFFE8DEF8), tertiary = Color(0xFF00897B), tertiaryContainer = Color(0xFFB2F1E8),
    background = Color(0xFFF8F5FF), surface = Color(0xFFF8F5FF), surfaceContainer = Color(0xFFF0ECF8),
    surfaceContainerHigh = Color(0xFFEAE6F2), error = Color(0xFFBA1A1A),
)
private val Dark = darkColorScheme(
    primary = Color(0xFFCFBCFF), onPrimary = Color(0xFF381E72), primaryContainer = Color(0xFF4F378B), onPrimaryContainer = Color(0xFFE9DDFF),
    tertiary = Color(0xFF80CBC4), tertiaryContainer = Color(0xFF00504A),
    background = Color(0xFF141218), surface = Color(0xFF141218), surfaceContainer = Color(0xFF211F26), surfaceContainerHigh = Color(0xFF2B2930),
)

@Composable
fun SklejkaTheme(mode: ThemeMode, dynamic: Boolean, content: @Composable () -> Unit) {
    val dark = when (mode) { ThemeMode.SYSTEM -> isSystemInDarkTheme(); ThemeMode.LIGHT -> false; ThemeMode.DARK -> true }
    val ctx = LocalContext.current
    val scheme = when {
        dynamic && Build.VERSION.SDK_INT >= 31 -> if (dark) dynamicDarkColorScheme(ctx) else dynamicLightColorScheme(ctx)
        dark -> Dark
        else -> Light
    }
    MaterialTheme(colorScheme = scheme, content = content)
}
