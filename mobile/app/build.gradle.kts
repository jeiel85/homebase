import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

// --- Version: one source of truth with the Windows release (DECISIONS M-4) -------------------
// CI passes -Phomebase.version=<tag without v>. versionCode = major*10000 + minor*100 + patch so
// the updater's integer comparison follows the tag order.
val homebaseVersion = providers.gradleProperty("homebase.version").orElse("0.14.0").get()
val homebaseVersionCode = homebaseVersion.split('.', '-').take(3).map { it.toIntOrNull() ?: 0 }
    .let { (maj, min, pat) -> maj * 10000 + min * 100 + pat }

// --- Update manifest URL --------------------------------------------------------------------
// Fixed GitHub "latest release" asset. Only a debug build may point elsewhere (and over http)
// for local end-to-end verification of the update flow.
val defaultManifestUrl = "https://github.com/jeiel85/homebase/releases/latest/download/update.json"
val debugManifestUrl = providers.gradleProperty("homebase.updateManifestUrl").orElse(defaultManifestUrl).get()

// --- Release signing --------------------------------------------------------------------------
val signingProps = Properties().apply {
    rootProject.file("release-signing.properties").takeIf { it.exists() }?.inputStream()?.use(::load)
}
fun signing(name: String): String? = providers.environmentVariable(name).orNull ?: signingProps.getProperty(name)
val storeFilePath = signing("HOMEBASE_RELEASE_STORE_FILE")
val hasReleaseSigning = listOf(
    storeFilePath,
    signing("HOMEBASE_RELEASE_STORE_PASSWORD"),
    signing("HOMEBASE_RELEASE_KEY_ALIAS"),
    signing("HOMEBASE_RELEASE_KEY_PASSWORD"),
).all { !it.isNullOrBlank() }
if (providers.gradleProperty("homebase.requireReleaseSigning").orNull == "true" && !hasReleaseSigning) {
    throw GradleException("Release signing values are missing (HOMEBASE_RELEASE_STORE_FILE/STORE_PASSWORD/KEY_ALIAS/KEY_PASSWORD).")
}

android {
    namespace = "io.github.jeiel85.homebase"
    compileSdk = 35

    defaultConfig {
        applicationId = "io.github.jeiel85.homebase"
        minSdk = 26
        targetSdk = 35
        versionCode = homebaseVersionCode
        versionName = homebaseVersion
        buildConfigField("String", "UPDATE_MANIFEST_URL", "\"$defaultManifestUrl\"")
        buildConfigField("boolean", "UPDATE_ALLOW_HTTP", "false")
    }

    signingConfigs {
        if (hasReleaseSigning) {
            create("release") {
                storeFile = rootProject.file(storeFilePath!!)
                storePassword = signing("HOMEBASE_RELEASE_STORE_PASSWORD")
                keyAlias = signing("HOMEBASE_RELEASE_KEY_ALIAS")
                keyPassword = signing("HOMEBASE_RELEASE_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        debug {
            buildConfigField("String", "UPDATE_MANIFEST_URL", "\"$debugManifestUrl\"")
            buildConfigField("boolean", "UPDATE_ALLOW_HTTP",
                providers.gradleProperty("homebase.updateAllowHttp").orElse("false").get())
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            if (hasReleaseSigning) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    dependenciesInfo {
        includeInApk = false
        includeInBundle = false
    }
    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.12.01")
    implementation(composeBom)
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material:material-icons-core")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    implementation("com.google.zxing:core:3.5.3")

    testImplementation("junit:junit:4.13.2")
    // Real org.json on the JVM test classpath (android.jar only ships stubs).
    testImplementation("org.json:json:20240303")
}
