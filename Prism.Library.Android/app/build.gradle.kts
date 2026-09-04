// Build-файл модуля приложения.
plugins {
    id("com.android.application")       // это Android-приложение
    id("org.jetbrains.kotlin.android")  // на языке Kotlin
    id("org.jetbrains.kotlin.plugin.serialization") // разбор JSON-ответов библиотеки
}

android {
    // namespace — базовое имя пакета для сгенерированного кода.
    namespace = "org.prism.library"
    // compileSdk — версия Android SDK, ПРОТИВ которой компилируем (что доступно в коде).
    compileSdk = 36

    defaultConfig {
        applicationId = "org.prism.library" // уникальный id приложения в системе
        minSdk = 24                         // минимальная версия Android (7.0); покрывает Android 11 бокса
        targetSdk = 36                      // под какую версию тестировали
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

// Подключаемые библиотеки.
dependencies {
    // RecyclerView — список содержимого текущей папки.
    implementation("androidx.recyclerview:recyclerview:1.3.2")
    // OkHttp — HTTP-запросы к Prism.Library (дерево, каталог файлов).
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    // kotlinx.serialization — разбор JSON-ответов в модели данных (Models.kt).
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
}
