package org.prism.player

import org.junit.Assert.*
import org.junit.Test
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

class MqttControllerTest {
    // Real MQTT transport against a loopback broker; no Android runtime required.
    private class Broker(private val dropFirst: Boolean = false, private val stallConnect: Boolean = false) : AutoCloseable {
        val server = ServerSocket(0)
        val connections = AtomicInteger()
        val subscriptions = AtomicInteger()
        val disconnected = CountDownLatch(1)
        val accepted = CountDownLatch(1)
        private val sockets = ConcurrentLinkedQueue<Socket>()
        @Volatile private var running = true
        val url get() = "tcp://127.0.0.1:${server.localPort}"

        init {
            Thread {
                while (running) {
                    val socket = try { server.accept() } catch (_: Exception) { break }
                    sockets.add(socket)
                    val number = connections.incrementAndGet()
                    accepted.countDown()
                    Thread {
                        try {
                            socket.soTimeout = 12000
                            val input = socket.getInputStream()
                            val output = socket.getOutputStream()
                            while (running) {
                                val header = input.read()
                                if (header < 0) break
                                var length = 0
                                var multiplier = 1
                                do {
                                    val digit = input.read()
                                    if (digit < 0) return@Thread
                                    length += (digit and 127) * multiplier
                                    multiplier *= 128
                                } while (digit and 128 != 0)
                                val body = input.readNBytes(length)
                                when (header shr 4) {
                                    1 -> if (!stallConnect) output.write(byteArrayOf(0x20, 2, 0, 0))
                                    8 -> {
                                        output.write(byteArrayOf(0x90.toByte(), 3, body[0], body[1], 1))
                                        output.flush()
                                        subscriptions.incrementAndGet()
                                        if (dropFirst && number == 1) break
                                    }
                                    12 -> output.write(byteArrayOf(0xd0.toByte(), 0))
                                    14 -> break
                                }
                                output.flush()
                            }
                        } catch (_: Exception) {
                        } finally {
                            socket.close()
                            disconnected.countDown()
                        }
                    }.apply { isDaemon = true; start() }
                }
            }.apply { isDaemon = true; start() }
        }

        override fun close() {
            running = false
            server.close()
            sockets.forEach { it.close() }
        }
    }

    private fun client(url: String, dispatch: (() -> Unit) -> Unit = { it() }, connected: () -> Unit = {}) =
        MqttController(url, "test-player", "prism/player/test/cmd", "", "",
            {}, {}, connected, {}, dispatch, {})

    @Test fun connectsSubscribesAndClosesSocketOnStop() {
        Broker().use { broker ->
            val connected = CountDownLatch(1)
            val mqtt = client(broker.url, connected = { connected.countDown() })
            try {
                mqtt.start()
                mqtt.start()
                assertTrue(connected.await(8, TimeUnit.SECONDS))
                assertEquals(1, broker.connections.get())
                assertEquals(1, broker.subscriptions.get())
                mqtt.stop()
                assertTrue(broker.disconnected.await(8, TimeUnit.SECONDS))
                mqtt.publish("test", "ignored after stop", true)
                mqtt.stop()
            } finally { mqtt.stop() }
        }
    }

    @Test fun reconnectsAndResubscribesAfterBrokerDisconnects() {
        Broker(dropFirst = true).use { broker ->
            val connected = CountDownLatch(2)
            val mqtt = client(broker.url, connected = { connected.countDown() })
            try {
                mqtt.start()
                assertTrue("Did not reconnect and subscribe", connected.await(12, TimeUnit.SECONDS))
                assertTrue(broker.subscriptions.get() >= 2)
            } finally { mqtt.stop() }
        }
    }

    @Test fun callbacksQueuedBeforeStopCannotUpdateDestroyedService() {
        Broker().use { broker ->
            val queued = CountDownLatch(1)
            val callbacks = ConcurrentLinkedQueue<() -> Unit>()
            val delivered = AtomicInteger()
            val mqtt = client(broker.url, dispatch = { callbacks.add(it); queued.countDown() },
                connected = { delivered.incrementAndGet() })
            try {
                mqtt.start()
                assertTrue(queued.await(8, TimeUnit.SECONDS))
                mqtt.stop()
                callbacks.forEach { it() }
                assertEquals(0, delivered.get())
                assertTrue(broker.disconnected.await(8, TimeUnit.SECONDS))
            } finally { mqtt.stop() }
        }
    }

    @Test fun rejectsInvalidConfigurationAndEmbeddedCredentials() {
        listOf("", "localhost", "https://host", "tcp://", "tcp://host:0", "tcp://host:65536",
            "tcp://user:password@host", "tcp://host/path", "tcp://host?password=secret").forEach {
            assertFalse(it, MqttEndpoint.isValid(it))
            val mqtt = client(it, connected = { fail("Connected with invalid configuration") })
            mqtt.start()
            mqtt.stop()
        }
        listOf("tcp://localhost:1883", "ssl://host:8883", "ws://host/mqtt", "wss://host/mqtt",
            "tcp://[::1]:1883").forEach { assertTrue(it, MqttEndpoint.isValid(it)) }
    }

    @Test fun stopDuringConnectionTimeoutClosesSocketWithoutRetry() {
        Broker(stallConnect = true).use { broker ->
            val mqtt = client(broker.url)
            try {
                mqtt.start()
                assertTrue(broker.accepted.await(3, TimeUnit.SECONDS))
                mqtt.stop()
                assertTrue("In-flight connection leaked", broker.disconnected.await(8, TimeUnit.SECONDS))
                assertEquals(1, broker.connections.get())
                assertEquals(0, broker.subscriptions.get())
            } finally { mqtt.stop() }
        }
    }
}
