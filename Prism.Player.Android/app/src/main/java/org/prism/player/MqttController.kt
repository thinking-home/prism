package org.prism.player

import android.os.Handler
import android.os.Looper
import org.eclipse.paho.client.mqttv3.IMqttDeliveryToken
import org.eclipse.paho.client.mqttv3.MqttCallback
import org.eclipse.paho.client.mqttv3.MqttClient
import org.eclipse.paho.client.mqttv3.MqttConnectOptions
import org.eclipse.paho.client.mqttv3.MqttMessage
import org.eclipse.paho.client.mqttv3.persist.MemoryPersistence
import org.json.JSONObject

// Подключается к MQTT-брокеру, слушает команды и передаёт их наружу двумя
// колбэками: onOpen(mediaId) и onClose(). Сам плеер здесь не трогаем — этим
// занимается сервис (там команды выполняются в главном потоке).
class MqttController(
    private val onOpen: (mediaId: String) -> Unit,
    private val onClose: () -> Unit,
) {
    // Главный поток — на нём живёт плеер, туда переключаемся перед вызовом колбэков.
    private val main = Handler(Looper.getMainLooper())
    private var client: MqttClient? = null

    fun start() {
        // Сеть нельзя в главном потоке, поэтому подключаемся в отдельном потоке.
        Thread {
            try {
                val c = MqttClient(BROKER_URL, CLIENT_ID, MemoryPersistence())
                val options = MqttConnectOptions().apply {
                    isAutomaticReconnect = true   // сам переподключается при обрыве
                    isCleanSession = true
                }
                c.setCallback(object : MqttCallback {
                    override fun connectionLost(cause: Throwable?) {}
                    override fun deliveryComplete(token: IMqttDeliveryToken?) {}
                    override fun messageArrived(topic: String, message: MqttMessage) {
                        handle(String(message.payload))
                    }
                })
                c.connect(options)
                c.subscribe(CMD_TOPIC)
                client = c
            } catch (e: Exception) {
                // Не удалось подключиться — не роняем приложение.
            }
        }.start()
    }

    fun stop() {
        val c = client
        client = null
        Thread { try { c?.disconnectForcibly(); c?.close() } catch (_: Exception) {} }.start()
    }

    // Разбираем JSON-команду и вызываем нужный колбэк В ГЛАВНОМ ПОТОКЕ.
    private fun handle(payload: String) {
        val json = try { JSONObject(payload) } catch (e: Exception) { return }
        when (json.optString("action")) {
            "open" -> {
                val id = json.optString("mediaId")
                if (id.isNotEmpty()) main.post { onOpen(id) }
            }
            "close" -> main.post { onClose() }
        }
    }

    companion object {
        // ИЗМЕНИ под свой брокер. 10.0.2.2 = адрес компьютера из эмулятора.
        private const val BROKER_URL = "tcp://10.0.2.2:1883"
        private const val CLIENT_ID = "prism-player-emulator"
        // Топик, куда координатор шлёт команды этому плееру.
        private const val CMD_TOPIC = "prism/player/emulator/cmd"
    }
}
