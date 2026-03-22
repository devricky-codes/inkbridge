plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.inkbridge.android"
    compileSdk = 34

    signingConfigs {
        create("release") {
            storeFile = file("../inkbridge.jks")
            storePassword = "inkbridge123"
            keyAlias = "inkbridge"
            keyPassword = "inkbridge123"
        }
    }

    defaultConfig {
        applicationId = "com.inkbridge.android"
        minSdk = 29
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("release")
        }
    }

    buildFeatures {
        compose = true
    }
    composeOptions {
        kotlinCompilerExtensionVersion = "1.4.6"
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.10.1")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.6.1")
    implementation("androidx.activity:activity-compose:1.7.2")
    implementation(platform("androidx.compose:compose-bom:2023.08.00"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    
    // OkHttp for WebSockets
    implementation("com.squareup.okhttp3:okhttp:4.11.0")

    // ML Kit Ink Recognition
    implementation("com.google.mlkit:digital-ink-recognition:18.1.0")
}
