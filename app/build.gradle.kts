import java.util.Properties

// Release signing: keystore.properties locally (never committed) or SIGNING_* env vars in CI.
val ksProps = Properties().apply {
    val f = rootProject.file("keystore.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}
fun sv(key: String, env: String): String? = System.getenv(env) ?: ksProps.getProperty(key)

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "ru.ivanvorontsov.sklejka"
    compileSdk = 35
    defaultConfig {
        applicationId = "ru.ivanvorontsov.sklejka"
        minSdk = 26
        targetSdk = 35
        versionCode = (System.getenv("VERSION_CODE") ?: "1").toInt()
        versionName = System.getenv("VERSION_NAME") ?: "1.0.0"
        resourceConfigurations += listOf("ru", "en")
    }
    signingConfigs {
        create("release") {
            val store = sv("storeFile", "SIGNING_STORE_FILE")
            if (store != null) {
                storeFile = rootProject.file(store)
                storePassword = sv("storePassword", "SIGNING_STORE_PASSWORD")
                keyAlias = sv("keyAlias", "SIGNING_KEY_ALIAS")
                keyPassword = sv("keyPassword", "SIGNING_KEY_PASSWORD")
            }
        }
    }
    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            signingConfig = signingConfigs.getByName("release")
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions {
        jvmTarget = "17"
        freeCompilerArgs += listOf("-opt-in=androidx.compose.material3.ExperimentalMaterial3Api", "-opt-in=androidx.compose.foundation.ExperimentalFoundationApi", "-opt-in=androidx.compose.foundation.layout.ExperimentalLayoutApi")
    }
    buildFeatures { compose = true; buildConfig = true }
    packaging { resources { excludes += listOf("/META-INF/{AL2.0,LGPL2.1}", "/META-INF/*.version", "DebugProbesKt.bin", "kotlin-tooling-metadata.json", "/kotlin/**") } }
    dependenciesInfo { includeInApk = false; includeInBundle = false }
}

dependencies {
    val bom = platform("androidx.compose:compose-bom:2024.10.01")
    implementation(bom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-core")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
    testImplementation("junit:junit:4.13.2")
}
