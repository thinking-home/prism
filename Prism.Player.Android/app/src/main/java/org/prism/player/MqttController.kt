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
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

// Подключается к MQTT-брокеру, слушает команды и передаёт их наружу колбэками
// (onOpen(url)/onClose()/onRefresh()), а также умеет публиковать сообщения (publish).
// onConnected зовётся при (пере)подключении — сервис публикует туда info/state.
// Сам плеер здесь не трогаем — этим занимается сервис (в главном потоке).
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
    private val onConnected: () -> Unit,
    private val onRefresh: () -> Unit,
) {
    // Главный поток — на нём живёт плеер, туда переключаемся перед вызовом колбэков.
    private val main = Handler(Looper.getMainLooper())
    private var client: MqttClient? = null
    // Пока true — цикл подключения работает; при stop() ставим false и выходим.
    @Volatile private var running = false
    private var thread: Thread? = null
    // Отдельный поток для отправки — чтобы не трогать сеть из главного потока.
    private var publisher: ExecutorService? = null

    fun start() {
        // Не настроено (нет брокера или топика) — не подключаемся, служба живёт дальше.
        if (brokerUrl.isEmpty() || cmdTopic.isEmpty()) return
        running = true
        publisher = Executors.newSingleThreadExecutor()
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
                onConnected() // опубликовать info/state при (пере)подключении
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

    // Опубликовать сообщение в топик (в фоне — сеть нельзя из главного потока).
    fun publish(topic: String, payload: String, retain: Boolean) {
        val c = client ?: return
        publisher?.execute {
            try {
                if (c.isConnected) c.publish(topic, payload.toByteArray(), 1, retain)
            } catch (_: Exception) {}
        }
    }

    fun stop() {
        running = false
        thread?.interrupt() // прервать сон, если сейчас ждём между попытками
        thread = null
        publisher?.shutdownNow()
        publisher = null
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
            "refresh" -> main.post { onRefresh() } // немедленно перепубликовать статус
        }
    }

    companion object {
        // Интервал между попытками подключения к брокеру.
        private const val RETRY_DELAY_MS = 5000L
    }
}
