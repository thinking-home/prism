package org.prism.player

import android.content.Context

// Крошечный общий держатель состояния подключения к MQTT-брокеру.
// MqttController обновляет его из колбэков, MainActivity читает и слушает,
// чтобы показывать статус на экране. Всё в одном процессе — хватает volatile.
object MqttStatus {

    @Volatile
    var connected: Boolean = false
        private set

    var running: Boolean = false
        private set
    private val listeners = mutableSetOf<() -> Unit>()

    fun addListener(listener: () -> Unit) { listeners.add(listener) }
    fun removeListener(listener: () -> Unit) { listeners.remove(listener) }

    fun setRunning(value: Boolean) {
        running = value
        if (!value) connected = false
        listeners.toList().forEach { it() }
    }

    fun textResource(context: Context): Int = when {
        !MqttEndpoint.isValid(Settings.brokerUrl(context)) -> R.string.mqtt_not_configured
        Settings.remoteExplicitlyStopped(context) -> R.string.mqtt_stopped
        connected -> R.string.mqtt_connected
        else -> R.string.mqtt_disconnected
    }

    fun set(value: Boolean) {
        connected = value
        listeners.toList().forEach { it() }
    }
}
