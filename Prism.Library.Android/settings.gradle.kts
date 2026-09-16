// Настройки Gradle-проекта в целом.
// pluginManagement — откуда Gradle берёт плагины (Android, Kotlin).
pluginManagement {
    repositories {
        google()          // репозиторий Google — плагин и библиотеки Android
        mavenCentral()    // основной публичный репозиторий Java/Kotlin-библиотек
        gradlePluginPortal()
    }
}

// dependencyResolutionManagement — откуда берутся сами библиотеки (зависимости).
dependencyResolutionManagement {
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "Prism.Library.Android"
include(":app")   // в проекте один модуль — приложение, лежит в папке app/
