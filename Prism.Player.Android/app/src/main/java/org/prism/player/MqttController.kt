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
// Адрес брокера, id, топик и логин/пароль приходят из настроек (Settings).
class MqttController(
    private val brokerUrl: String,
    private val clientId: String,
    private val cmdTopic: String,
    private val mqttUser: String,
    private val mqttPassword: String,
    private val onOpen: (mediaId: String) -> Unit,
    private val onClose: () -> Unit,
) {
    // Главный поток — на нём живёт плеер, туда переключаемся перед вызовом колбэков.
    private val main = Handler(Looper.getMainLooper())
    private var client: MqttClient? = null

    fun start() {
        // Не настроено (нет брокера или топика) — не подключаемся, служба живёт дальше.
        if (brokerUrl.isEmpty() || cmdTopic.isEmpty()) return
        // Сеть нельзя в главном потоке, поэтому подключаемся в отдельном потоке.
        Thread {
            try {
                val c = MqttClient(brokerUrl, clientId, MemoryPersistence())
                val options = MqttConnectOptions().apply {
                    isAutomaticReconnect = true   // сам переподключается при обрыве
                    isCleanSession = true
                    // Логин/пароль — только если заданы (иначе анонимный вход).
                    if (mqttUser.isNotEmpty()) {
                        userName = mqttUser
                        password = mqttPassword.toCharArray()
                    }
                }
                c.setCallback(object : MqttCallback {
                    override fun connectionLost(cause: Throwable?) {}
                    override fun deliveryComplete(token: IMqttDeliveryToken?) {}
                    override fun messageArrived(topic: String, message: MqttMessage) {
                        handle(String(message.payload))
                    }
                })
                c.connect(options)
                c.subscribe(cmdTopic)
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
}
