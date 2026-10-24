package org.prism.player

import java.net.URI

object MqttEndpoint {
    fun isValid(value: String): Boolean = try {
        val uri = URI(value)
        uri.scheme in setOf("tcp", "ssl", "ws", "wss") &&
            !uri.host.isNullOrBlank() && uri.userInfo == null &&
            uri.fragment == null && uri.query == null &&
            (uri.port == -1 || uri.port in 1..65535) &&
            (uri.scheme in setOf("ws", "wss") || uri.path.isNullOrEmpty())
    } catch (_: Exception) {
        false
    }
}
