// Корневой build-файл: объявляем версии плагинов для всего проекта.
// apply false — здесь только фиксируем версии, а подключаются они в модуле app.
buildscript {
    dependencies {
        // AGP использует встроенный Kotlin; эта версия поддерживает JDK 25.
        classpath("org.jetbrains.kotlin:kotlin-gradle-plugin:2.3.0")
    }
}

plugins {
    id("com.android.application") version "9.0.1" apply false
}
