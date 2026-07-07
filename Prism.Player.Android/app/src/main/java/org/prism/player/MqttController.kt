package org.prism.player

import android.os.Handler
import android.os.Looper
import org.eclipse.paho.client.mqttv3.IMqttDeliveryToken
import org.eclipse.paho.client.mqttv3.MqttCallbackExtended
import org.eclipse.paho.client.mqttv3.MqttClient
import org.eclipse.paho.client.mqttv3.MqttConnectOptions
import org.eclipse.paho.client.mqttv3.MqttMessage
import org.eclipse.paho.client.mqttv3.persist.MemoryPersistence
import org.json.JSONObject

// Подключается к MQTT-брокеру, слушает команды и передаёт их наружу двумя
// колбэками: onOpen(url) и onClose(). Сам плеер здесь не трогаем — этим
// занимается сервис (там команды выполняются в главном потоке).
// Адрес брокера, id, топик и логин/пароль приходят из настроек (Settings).
// Подключение — с бесконечными повторами: если брокер недоступен, пробуем снова
// каждые несколько секунд; состояние связи публикуем в MqttStatus.
class MqttController(
    private val brokerUrl: String,
    private val clientId: String,
    private val cmdTopic: String,
    private val mqttUser: String,
    private val mqttPassword: String,
    private val onOpen: (url: String) -> Unit,
    private val onClose: () -> Unit,
) {
    // Главный поток — на нём живёт плеер, туда переключаемся перед вызовом колбэков.
    private val main = Handler(Looper.getMainLooper())
    private var client: MqttClient? = null
    // Пока true — цикл подключения работает; при stop() ставим false и выходим.
    @Volatile private var running = false
    private var thread: Thread? = null

    fun start() {
        // Не настроено (нет брокера или топика) — не подключаемся, служба живёт дальше.
        if (brokerUrl.isEmpty() || cmdTopic.isEmpty()) return
        running = true
        // Сеть нельзя в главном потоке — подключаемся в отдельном.
        thread = Thread { connectLoop() }.also { it.start() }
    }

    // Пробуем подключиться, пока не выйдет или пока не остановят. Ресурсы не текут:
    // один клиент и один поток, между попытками — сон.
    private fun connectLoop() {
        val c = MqttClient(brokerUrl, clientId, MemoryPersistence())
        client = c
        val options = MqttConnectOptions().apply {
            isAutomaticReconnect = true   // после первого коннекта Paho сам держит связь
            isCleanSession = true
            // Логин/пароль — только если заданы (иначе анонимный вход).
            if (mqttUser.isNotEmpty()) {
                userName = mqttUser
                password = mqttPassword.toCharArray()
            }
        }
        c.setCallback(object : MqttCallbackExtended {
            // Вызывается при первом подключении И при каждом авто-переподключении.
            override fun connectComplete(reconnect: Boolean, serverURI: String?) {
                try { c.subscribe(cmdTopic) } catch (_: Exception) {} // подписку надо возобновлять
                MqttStatus.set(true)
            }
            override fun connectionLost(cause: Throwable?) { MqttStatus.set(false) }
            override fun deliveryComplete(token: IMqttDeliveryToken?) {}
            override fun messageArrived(topic: String, message: MqttMessage) {
                handle(String(message.payload))
            }
        })
        while (running) {
            try {
                c.connect(options)
                return // подключились; дальнейшие обрывы держит isAutomaticReconnect
            } catch (e: Exception) {
                MqttStatus.set(false)
                try { Thread.sleep(RETRY_DELAY_MS) } catch (ie: InterruptedException) { return }
            }
        }
    }

    fun stop() {
        running = false
        thread?.interrupt() // прервать сон, если сейчас ждём между попытками
        thread = null
        val c = client
        client = null
        Thread { try { c?.disconnectForcibly(); c?.close() } catch (_: Exception) {} }.start()
    }

    // Разбираем JSON-команду и вызываем нужный колбэк В ГЛАВНОМ ПОТОКЕ.
    private fun handle(payload: String) {
        val json = try { JSONObject(payload) } catch (e: Exception) { return }
        when (json.optString("action")) {
            "open" -> {
                val url = json.optString("url")
                if (url.isNotEmpty()) main.post { onOpen(url) }
            }
            "close" -> main.post { onClose() }
        }
    }

    companion object {
        // Интервал между попытками подключения к брокеру.
        private const val RETRY_DELAY_MS = 5000L
    }
}
