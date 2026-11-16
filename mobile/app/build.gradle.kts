import com.android.build.api.variant.BuildConfigField
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
val debugAllowHttp = providers.gradleProperty("homebase.updateAllowHttp").orElse("false").get()
// Certificate SHA-256 of the key that signs the published APKs (DECISIONS M-12). The app compares
// its own signer with it so a debug-signed install explains why the update can't go on top of it.
// Empty (no check) when a debug build reads another manifest: local verification signs with any key.
val releaseCertSha256 = "4d9cbd4d68cd2ee792fd1f506cbfae9c2313186b668a5ca34d9020f5d23ef865"

// --- Release signing: one key per distribution channel (DECISIONS M-33) -----------------------
// github: the key phones already trust for in-app updates (M-4) — HOMEBASE_RELEASE_*.
// play:   the Play upload key (Play re-signs with its app signing key) — HOMEBASE_PLAY_UPLOAD_*.
// Each value comes from the environment (CI) or from the channel's properties file in mobile/
// (local release builds; gitignored, never committed).
class ChannelSigning(private val prefix: String, propertiesFile: String) {
    private val props = Properties().apply {
        rootProject.file(propertiesFile).takeIf { it.exists() }?.inputStream()?.use(::load)
    }
    private fun value(name: String): String? = providers.environmentVariable("${prefix}_$name").orNull ?: props.getProperty("${prefix}_$name")
    val storeFile = value("STORE_FILE")
    val storePassword = value("STORE_PASSWORD")
    val keyAlias = value("KEY_ALIAS")
    val keyPassword = value("KEY_PASSWORD")
    val complete = listOf(storeFile, storePassword, keyAlias, keyPassword).all { !it.isNullOrBlank() }
}
val githubSigning = ChannelSigning("HOMEBASE_RELEASE", "release-signing.properties")
val playSigning = ChannelSigning("HOMEBASE_PLAY_UPLOAD", "play-upload-signing.properties")
if (providers.gradleProperty("homebase.requireReleaseSigning").orNull == "true" && !githubSigning.complete) {
    throw GradleException("Release signing values are missing (HOMEBASE_RELEASE_STORE_FILE/STORE_PASSWORD/KEY_ALIAS/KEY_PASSWORD).")
}
if (providers.gradleProperty("homebase.requirePlaySigning").orNull == "true" && !playSigning.complete) {
    throw GradleException("Play upload signing values are missing (HOMEBASE_PLAY_UPLOAD_STORE_FILE/STORE_PASSWORD/KEY_ALIAS/KEY_PASSWORD).")
}

android {
    namespace = "io.github.jeiel85.homebase"
    // Google Play requires new apps and updates to target API 36 from 2026-08-31.
    compileSdk = 36

    defaultConfig {
        applicationId = "io.github.jeiel85.homebase"
        minSdk = 26
        targetSdk = 36
        versionCode = homebaseVersionCode
        versionName = homebaseVersion
        // The updater's BuildConfig fields exist in the GitHub build only — see androidComponents below.
    }

    signingConfigs {
        if (githubSigning.complete) {
            create("github") {
                storeFile = rootProject.file(githubSigning.storeFile!!)
                storePassword = githubSigning.storePassword
                keyAlias = githubSigning.keyAlias
                keyPassword = githubSigning.keyPassword
            }
        }
        if (playSigning.complete) {
            create("playUpload") {
                storeFile = rootProject.file(playSigning.storeFile!!)
                storePassword = playSigning.storePassword
                keyAlias = playSigning.keyAlias
                keyPassword = playSigning.keyPassword
            }
        }
    }

    // Distribution channel (DECISIONS M-33). Same applicationId — the Play Console app is
    // io.github.jeiel85.homebase — but different signing keys, so one cannot be installed over the other.
    //   github: sideloaded APK from GitHub Releases, updates itself (src/github: updater + REQUEST_INSTALL_PACKAGES).
    //   play:   AAB for Google Play, which updates it; no updater code or install permission at all.
    flavorDimensions += "channel"
    productFlavors {
        create("github") {
            dimension = "channel"
            if (githubSigning.complete) signingConfig = signingConfigs.getByName("github")
        }
        create("play") {
            dimension = "channel"
            if (playSigning.complete) signingConfig = signingConfigs.getByName("playUpload")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            // Signing comes from the channel flavor above; a build type's signingConfig would override it.
            // The AAB carries the symbol tables of the bundled native libraries (from dependencies), so
            // Play Console can symbolicate native crashes and ANRs (docs/PLAY_RELEASE.md).
            ndk { debugSymbolLevel = "SYMBOL_TABLE" }
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

// The in-app updater's BuildConfig fields, only in the GitHub build (DECISIONS M-33): the Play build,
// which Play updates, carries no GitHub update URL. A build type can't set a field for one flavor only,
// hence the variant API. Release builds always read the published manifest.
androidComponents {
    onVariants { variant ->
        if (variant.flavorName == "github") {
            val debug = variant.buildType == "debug"
            val manifestUrl = if (debug) debugManifestUrl else defaultManifestUrl
            variant.buildConfigFields?.put(
                "UPDATE_MANIFEST_URL",
                BuildConfigField("String", "\"$manifestUrl\"", "update.json the updater reads"),
            )
            variant.buildConfigFields?.put(
                "UPDATE_ALLOW_HTTP",
                BuildConfigField("boolean", if (debug) debugAllowHttp else "false", "plain http for local verification only"),
            )
            variant.buildConfigFields?.put(
                "UPDATE_SIGNER_SHA256",
                BuildConfigField(
                    "String",
                    "\"${if (manifestUrl == defaultManifestUrl) releaseCertSha256 else ""}\"",
                    "certificate SHA-256 of the published APKs; empty = not checked",
                ),
            )
        }
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
