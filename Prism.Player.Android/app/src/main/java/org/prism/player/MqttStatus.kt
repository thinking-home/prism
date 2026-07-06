package org.prism.player

// Крошечный общий держатель состояния подключения к MQTT-брокеру.
// MqttController обновляет его из колбэков, MainActivity читает и слушает,
// чтобы показывать статус на экране. Всё в одном процессе — хватает volatile.
object MqttStatus {

    @Volatile
    var connected: Boolean = false
        private set

    // Слушатель ставит открытый экран (и снимает при закрытии); зовётся при смене.
    var listener: ((Boolean) -> Unit)? = null

    fun set(value: Boolean) {
        connected = value
        listener?.invoke(value)
    }
}
