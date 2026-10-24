package org.prism.player

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

// Приёмник системного события: срабатывает, когда устройство загрузилось.
// Его задача — запустить нашу службу воспроизведения, чтобы MQTT слушал команды
// сразу после включения, даже без открытия приложения.
class BootReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == Intent.ACTION_BOOT_COMPLETED && Settings.canRunRemote(context)) {
            MqttService.startIfEnabled(context)
        }
    }
}
