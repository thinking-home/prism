package org.prism.player

import org.eclipse.paho.client.mqttv3.IMqttDeliveryToken
import org.eclipse.paho.client.mqttv3.MqttCallback
import org.eclipse.paho.client.mqttv3.MqttClient
import org.eclipse.paho.client.mqttv3.MqttConnectOptions
import org.eclipse.paho.client.mqttv3.MqttMessage
import org.eclipse.paho.client.mqttv3.persist.MemoryPersistence
import org.json.JSONObject
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.TimeUnit

// One worker owns the client, including connect, subscribe, publish and close.
// No Paho automatic reconnect survives stop(); callbacks are delivered on the UI thread.
class MqttController(
    private val brokerUrl: String,
    private val clientId: String,
    private val cmdTopic: String,
    private val mqttUser: String,
    private val mqttPassword: String,
    private val onOpen: (String) -> Unit,
    private val onClose: () -> Unit,
    private val onConnected: () -> Unit,
    private val onRefresh: () -> Unit,
    private val dispatch: (() -> Unit) -> Unit,
    private val onConnectionChange: (Boolean) -> Unit,
) {
    private val worker = Executors.newSingleThreadScheduledExecutor()
    private var client: MqttClient? = null // accessed only by worker
    @Volatile private var running = false
    private var started = false

    @Synchronized
    fun start() {
        if (started) return
        started = true
        if (!MqttEndpoint.isValid(brokerUrl) || cmdTopic.isBlank()) {
            worker.shutdown()
            return
        }
        running = true
        worker.execute { connect() }
    }

    private fun deliver(callback: () -> Unit) {
        if (running) dispatch { if (running) callback() }
    }

    private fun connect() {
        if (!running) return
        try {
            val c = client ?: MqttClient(brokerUrl, clientId, MemoryPersistence()).also {
                client = it
                it.setCallback(object : MqttCallback {
                    override fun connectionLost(cause: Throwable?) {
                        deliver { onConnectionChange(false) }
                        // A single scheduled loop below owns reconnection.
                    }
                    override fun deliveryComplete(token: IMqttDeliveryToken?) {}
                    override fun messageArrived(topic: String, message: MqttMessage) {
                        if (running) handle(message.payload.toString(Charsets.UTF_8))
                    }
                })
            }
            if (!c.isConnected) {
                val options = MqttConnectOptions().apply {
                    isAutomaticReconnect = false
                    isCleanSession = true
                    connectionTimeout = 5
                    keepAliveInterval = 30
                    if (mqttUser.isNotEmpty()) {
                        userName = mqttUser
                        password = mqttPassword.toCharArray()
                    }
                }
                c.setTimeToWait(5000)
                c.connect(options)
                if (!running) return
                c.subscribe(cmdTopic)
                deliver { onConnectionChange(true); onConnected() }
            }
        } catch (_: Exception) {
            closeClient()
            deliver { onConnectionChange(false) }
        } finally {
            if (running) {
                try { worker.schedule({ connect() }, 5, TimeUnit.SECONDS) }
                catch (_: RejectedExecutionException) { /* stopped concurrently */ }
            }
        }
    }

    fun publish(topic: String, payload: String, retain: Boolean) {
        if (!running) return
        try {
            worker.execute {
                if (running) {
                    try {
                        client?.takeIf { it.isConnected }?.publish(topic, payload.toByteArray(Charsets.UTF_8), 1, retain)
                    } catch (_: Exception) { /* connection loop retries */ }
                }
            }
        } catch (_: RejectedExecutionException) { /* stopped concurrently */ }
    }

    @Synchronized
    fun stop() {
        running = false
        started = true // this controller is single-use
        if (worker.isShutdown) return
        // Connection timeout bounds cleanup, without blocking the main thread.
        worker.execute { closeClient() }
        worker.shutdown()
    }

    private fun closeClient() {
        val c = client ?: return
        client = null
        try { c.disconnectForcibly(0, 1000, false) } catch (_: Exception) {}
        try { c.close(true) } catch (_: Exception) {}
    }

    private fun handle(payload: String) {
        val json = try { JSONObject(payload) } catch (_: Exception) { return }
        when (json.optString("action")) {
            "open" -> {
                val url = json.optString("url")
                if (url.isNotEmpty()) deliver { onOpen(url) }
            }
            "close" -> deliver { onClose() }
            "refresh" -> deliver { onRefresh() }
        }
    }
}