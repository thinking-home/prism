package org.prism.player

import android.content.Context
import java.util.UUID

// Хранилище настроек в SharedPreferences (простой файл «ключ-значение» внутри
// приложения). Одно место, откуда службы читают адреса и идентификаторы.
// Значения по умолчанию: в debug-сборке — как на эмуляторе (10.0.2.2),
// в release — пустые (пользователь вводит их на экране настроек).
object Settings {

    private const val PREFS = "prism_settings"

    private const val KEY_BROKER_URL = "broker_url"
    private const val KEY_MQTT_USER = "mqtt_user"
    private const val KEY_MQTT_PASSWORD = "mqtt_password"
    private const val KEY_PLAYER_NAME = "player_name"
    private const val KEY_PLAYER_ID = "player_id"

    // Значения для эмулятора — подставляются только в отладочной сборке.
    private const val DEBUG_BROKER_URL = "tcp://10.0.2.2:1883"
    private const val DEBUG_PLAYER_ID = "emulator"

    private fun prefs(ctx: Context) =
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    // Адрес MQTT-брокера. Нет сохранённого значения → дефолт (debug: эмулятор, иначе пусто).
    fun brokerUrl(ctx: Context): String =
        prefs(ctx).getString(KEY_BROKER_URL, null)
            ?: if (BuildConfig.DEBUG) DEBUG_BROKER_URL else ""

    // Логин/пароль MQTT — необязательны (пусто = анонимный вход).
    fun mqttUser(ctx: Context): String =
        prefs(ctx).getString(KEY_MQTT_USER, null) ?: ""

    fun mqttPassword(ctx: Context): String =
        prefs(ctx).getString(KEY_MQTT_PASSWORD, null) ?: ""

    // Идентификатор плеера — не редактируется. В debug — предопределённый «emulator»
    // (удобно слать команды в известный топик). В release — генерируем уникальный
    // GUID один раз и сохраняем; дальше он не меняется.
    fun playerId(ctx: Context): String {
        if (BuildConfig.DEBUG) return DEBUG_PLAYER_ID
        val p = prefs(ctx)
        val existing = p.getString(KEY_PLAYER_ID, null)
        if (existing != null) return existing
        val generated = UUID.randomUUID().toString()
        p.edit().putString(KEY_PLAYER_ID, generated).apply()
        return generated
    }

    // Отображаемое имя плеера (человеческая подпись). Может быть пустым — подстановку
    // id вместо пустого имени сделаем позже, когда будем публиковать статус info.
    fun playerName(ctx: Context): String =
        prefs(ctx).getString(KEY_PLAYER_NAME, null) ?: ""

    // Вычисляемые из id: топики и client-id подключения к брокеру.
    fun cmdTopic(ctx: Context): String = "prism/player/${playerId(ctx)}/cmd"
    fun infoTopic(ctx: Context): String = "prism/player/${playerId(ctx)}/info"
    fun clientId(ctx: Context): String = "prism-player-${playerId(ctx)}"

    // Сохранить редактируемые поля разом (id не редактируется — его не трогаем).
    fun save(
        ctx: Context,
        brokerUrl: String,
        mqttUser: String,
        mqttPassword: String,
        playerName: String,
    ) {
        prefs(ctx).edit()
            .putString(KEY_BROKER_URL, brokerUrl)
            .putString(KEY_MQTT_USER, mqttUser)
            .putString(KEY_MQTT_PASSWORD, mqttPassword)
            .putString(KEY_PLAYER_NAME, playerName)
            .apply()
    }
}
