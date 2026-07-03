// Build-файл модуля приложения.
plugins {
    id("com.android.application")       // это Android-приложение
    id("org.jetbrains.kotlin.android")  // на языке Kotlin
}

android {
    // namespace — базовое имя пакета для сгенерированного кода.
    namespace = "org.prism.player"
    // compileSdk — версия Android SDK, ПРОТИВ которой компилируем (что доступно в коде).
    compileSdk = 36

    defaultConfig {
        applicationId = "org.prism.player" // уникальный id приложения в системе
        minSdk = 24                        // минимальная версия Android (7.0); покрывает Android 11 бокса
        targetSdk = 36                     // под какую версию тестировали
        versionCode = 1
        versionName = "0.1"
    }

    // Компилируем Java/Kotlin под уровень языка 17 (JDK 21 это умеет).
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}
