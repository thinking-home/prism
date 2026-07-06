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

    // Включаем генерацию класса BuildConfig — нужен флаг BuildConfig.DEBUG,
    // чтобы подставлять настройки эмулятора только в отладочной сборке.
    buildFeatures {
        buildConfig = true
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
    // ExoPlayer (Media3) — проигрыватель видео.
    implementation("androidx.media3:media3-exoplayer:1.4.1")
    // Поддержка HLS (формат, которым Prism отдаёт видео).
    implementation("androidx.media3:media3-exoplayer-hls:1.4.1")
    // Готовый экран проигрывателя (PlayerView) с элементами управления.
    implementation("androidx.media3:media3-ui:1.4.1")
    // Медиа-сессия: MediaSession, MediaSessionService, MediaController.
    implementation("androidx.media3:media3-session:1.4.1")
    // MQTT-клиент (Paho) — приём команд управления.
    implementation("org.eclipse.paho:org.eclipse.paho.client.mqttv3:1.2.5")
}
