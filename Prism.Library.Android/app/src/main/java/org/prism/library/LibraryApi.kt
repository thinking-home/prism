package org.prism.library

import android.os.Handler
import android.os.Looper
import java.io.IOException
import kotlinx.serialization.json.Json
import okhttp3.Call
import okhttp3.Callback
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response

// Минимальный HTTP-клиент библиотеки Prism: два запроса, нужных для показа
// содержимого папки. Запросы асинхронные (OkHttp сам уводит их в фоновый
// поток), результат приходит в переданный обработчик уже на UI-потоке — его
// можно сразу использовать для обновления экрана.
object LibraryApi {

    // Один клиент на всё приложение — так рекомендует сама OkHttp (внутри
    // общий пул соединений и потоков).
    private val client = OkHttpClient()

    // ignoreUnknownKeys — ответы сервера содержат больше полей, чем описано в
    // наших моделях (Models.kt); лишние поля просто пропускаются при разборе.
    private val json = Json { ignoreUnknownKeys = true }

    // GET /api/library/tree — все группы и membership файлов одним запросом.
    fun getTree(baseUrl: String, onResult: (Result<LibraryTree>) -> Unit) {
        get(baseUrl, "/api/library/tree", onResult) { json.decodeFromString(it) }
    }

    // GET /api/media — объединённый каталог файлов, видимых сейчас хотя бы на одном хосте.
    fun getMedia(baseUrl: String, onResult: (Result<List<MediaCard>>) -> Unit) {
        get(baseUrl, "/api/media", onResult) { json.decodeFromString(it) }
    }

    // Общая часть обоих запросов: выполнить GET, разобрать тело парсером,
    // вернуть результат в обработчик на UI-потоке (и при успехе, и при ошибке).
    private fun <T> get(
        baseUrl: String,
        path: String,
        onResult: (Result<T>) -> Unit,
        parse: (String) -> T,
    ) {
        val mainThread = Handler(Looper.getMainLooper())

        // baseUrl приходит из настроек как обычный текст — пользователь мог
        // ввести его без "http://" или вовсе не URL. Request.Builder.url()
        // в этом случае бросает исключение синхронно, до какого-либо колбэка;
        // без этого try/catch оно улетело бы прямо в вызывающий код на
        // UI-потоке и уронило бы приложение. Ловим его здесь и отдаём тем же
        // путём, что и сетевые ошибки, — через onResult(Result.failure).
        val request = try {
            Request.Builder().url(baseUrl.trimEnd('/') + path).build()
        } catch (e: IllegalArgumentException) {
            mainThread.post { onResult(Result.failure(e)) }
            return
        }

        client.newCall(request).enqueue(object : Callback {
            // Сеть недоступна, адрес неверный и т.п. — соединение не установилось.
            override fun onFailure(call: Call, e: IOException) {
                mainThread.post { onResult(Result.failure(e)) }
            }

            // Соединение установилось — но код ответа или содержимое ещё может
            // быть ошибочным, поэтому всё равно оборачиваем в try/catch.
            override fun onResponse(call: Call, response: Response) {
                val result = try {
                    response.use {
                        if (!it.isSuccessful) throw IOException("HTTP ${it.code}")
                        val body = it.body?.string() ?: throw IOException("Empty response body")
                        Result.success(parse(body))
                    }
                } catch (e: Exception) {
                    Result.failure(e)
                }
                mainThread.post { onResult(result) }
            }
        })
    }
}
