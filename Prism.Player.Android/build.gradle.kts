// Корневой build-файл: объявляем версии плагинов для всего проекта.
// apply false — здесь только фиксируем версии, а подключаются они в модуле app.
plugins {
    id("com.android.application") version "8.10.0" apply false
    id("org.jetbrains.kotlin.android") version "2.0.21" apply false
}
