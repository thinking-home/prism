package org.prism.library

import kotlinx.serialization.Serializable

// Модели данных библиотеки — только поля, которые нужны для списка папки на
// этом шаге. Остальные поля ответа (например, длительность видео или мета)
// пока не нужны и в модели не описаны — kotlinx.serialization их просто
// проигнорирует при разборе.

// Одна группа дерева библиотеки: id, id родителя (null — группа верхнего
// уровня) и отображаемое имя.
@Serializable
data class LibraryNode(
    val id: String,
    val parentId: String? = null,
    val name: String,
)

// Членство одного файла в одной группе. present — файл сейчас доступен хотя
// бы на одном хосте; используется, когда мы показываем содержимое группы
// (шаги 4–5), а не корень.
@Serializable
data class LibraryItem(
    val nodeId: String,
    val mediaId: String,
    val present: Boolean,
)

// Ответ GET /api/library/tree целиком: все группы и всё членство одним запросом.
@Serializable
data class LibraryTree(
    val nodes: List<LibraryNode>,
    val items: List<LibraryItem>,
)

// Карточка файла из GET /api/media. present по умолчанию true: этот список
// содержит только файлы, реально найденные на каком-то хосте сейчас — сам
// эндпоинт отсутствующие файлы не возвращает.
@Serializable
data class MediaCard(
    val id: String,
    val title: String,
    val present: Boolean = true,
)

// Подробная карточка файла из GET /api/media/{id} — для шторки «информация»
// (шаг 6). Поля — подмножество полного ответа хоста (см. Prism.Host,
// PrismHostApp.MediaDto): только то, что стоит показать пользователю. Числовые
// поля не обязательны — сервер не гарантирует их для всех форматов, но всегда
// присылает значение (0 по умолчанию, не null), поэтому default не нужен.
@Serializable
data class MediaDetail(
    val id: String,
    val title: String,
    val durationSeconds: Double,
    val width: Int,
    val height: Int,
    val videoCodec: String? = null,
    val audioCodec: String? = null,
    val host: String? = null,
    val playable: Boolean = false,
)
