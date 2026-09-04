package org.prism.library

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.LinearLayout
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView

// Главный экран приложения: список содержимого текущей папки библиотеки, с
// переходом внутрь групп и подъёмом наверх (аналог проводника, без дерева на
// экране), плюс кнопка-шестерёнка для перехода к настройке адреса.
class MainActivity : Activity() {

    private lateinit var headerText: TextView
    private lateinit var statusText: TextView
    private lateinit var list: RecyclerView

    // Адрес библиотеки — читаем один раз при входе в приложение (onStart);
    // переход по папкам его не меняет.
    private var libraryUrl: String = ""

    // Стек посещённых групп: узлы от корня до текущей папки (не только id —
    // ещё и имя, для заголовка в шапке). Пустой стек — мы в корне библиотеки,
    // подниматься некуда. Переход в группу — добавление её узла в конец;
    // системная кнопка «назад» — единственный способ подняться, убирает
    // последний узел (отдельной кнопки/стрелки «наверх» в интерфейсе нет —
    // решение design.md).
    private val nodeStack = ArrayDeque<LibraryNode>()

    // Счётчик запросов загрузки папки: у каждого вызова loadFolder — свой
    // номер. Ответы двух эндпоинтов (дерево и каталог) приходят асинхронно и
    // независимо; если за время ожидания пользователь успел перейти в другую
    // папку (новый loadFolder успел стартовать), результат устаревшего запроса
    // приходит позже, но применять его к экрану уже нельзя — иначе список
    // покажет содержимое папки, из которой пользователь уже вышел (исправление
    // гонки, задача 4.0).
    private var loadGeneration = 0

    // onCreate вызывается один раз при создании экрана — здесь мы его собираем.
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Список содержимого папки — заполняется после загрузки с сервера
        // (см. loadFolder). LinearLayoutManager — элементы друг под другом.
        list = RecyclerView(this).apply {
            layoutManager = LinearLayoutManager(this@MainActivity)
        }

        // Текст состояния — «загрузка…», ошибка или «папка пуста». Показывается
        // поверх списка по центру, пока список пуст/не загружен.
        statusText = TextView(this).apply {
            textSize = 18f
        }

        // Заголовок текущей папки — имя группы или обозначение корня
        // (см. updateHeader). Единственный способ увидеть, где мы сейчас:
        // отдельной кнопки/стрелки «наверх» в интерфейсе нет — подняться можно
        // только системной кнопкой «назад» (решение design.md).
        val headerPad = (16 * resources.displayMetrics.density).toInt()
        headerText = TextView(this).apply {
            textSize = 20f
            setPadding(headerPad, headerPad, headerPad, headerPad)
            layoutParams = LinearLayout.LayoutParams(
                0,
                LinearLayout.LayoutParams.WRAP_CONTENT,
                1f, // растягивается на всё свободное место рядом с шестерёнкой
            )
        }

        // Кнопка-шестерёнка справа в шапке — открывает экран настроек.
        // Плоский безрамочный фон (штатный selectableItemBackgroundBorderless):
        // прозрачный, с круглой подсветкой при фокусе/нажатии — удобно и для
        // касания, и для пульта.
        val settingsButton = ImageButton(this).apply {
            setImageResource(R.drawable.ic_settings)
            val bg = TypedValue()
            context.theme.resolveAttribute(
                android.R.attr.selectableItemBackgroundBorderless, bg, true,
            )
            setBackgroundResource(bg.resourceId)
            val innerPad = (12 * resources.displayMetrics.density).toInt()
            setPadding(innerPad, innerPad, innerPad, innerPad)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT,
                0f,
            ).apply { gravity = Gravity.CENTER_VERTICAL }
            setOnClickListener {
                startActivity(Intent(this@MainActivity, SettingsActivity::class.java))
            }
        }

        // Шапка — горизонтальная полоса сверху экрана: заголовок папки слева,
        // шестерёнка настроек справа.
        val header = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            addView(headerText)
            addView(settingsButton)
        }

        // Содержимое ниже шапки: список на весь экран, статус — поверх него
        // по центру, пока список пуст/не загружен.
        val content = FrameLayout(this).apply {
            addView(
                list,
                FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT,
                    FrameLayout.LayoutParams.MATCH_PARENT,
                ),
            )
            addView(
                statusText,
                FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    Gravity.CENTER,
                ),
            )
        }

        // Экран целиком: шапка сверху, содержимое папки — под ней на весь
        // остаток экрана.
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(header)
            addView(
                content,
                LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    0,
                    1f,
                ),
            )
        }

        setContentView(root)
    }

    // onStart вызывается каждый раз, когда экран становится видимым — в том
    // числе при возврате с экрана настроек. Проверяем актуальный адрес заново,
    // а не только один раз при создании экрана, и начинаем заново с корня —
    // адрес мог смениться на другую библиотеку, прежняя папка ей не подходит.
    override fun onStart() {
        super.onStart()

        val url = Settings.libraryUrl(this)

        // Адрес библиотеки ещё не задан — открываем экран настроек вместо
        // главного экрана. Пользователь не сможет пользоваться приложением,
        // пока не введёт адрес (см. settings/spec.md, сценарий «Первый запуск»).
        //
        // finish() здесь обязателен: без него этот экран остаётся под настройками
        // в стеке, и системная кнопка «назад» из настроек просто вернёт сюда же —
        // onStart снова откроет настройки, и выйти из приложения будет нельзя
        // (особенно важно на ТВ, где «назад» — единственный способ уйти пультом).
        // Убрав себя из стека, мы делаем «назад» из настроек обычным выходом.
        if (url.isEmpty()) {
            startActivity(Intent(this, SettingsActivity::class.java))
            finish()
            return
        }

        libraryUrl = url
        nodeStack.clear()
        updateHeader()
        loadFolder()
    }

    // Системная кнопка «назад»: если мы не в корне — поднимаемся на уровень
    // выше вместо выхода из приложения; в корне — обычное поведение (выход).
    // Отдельной кнопки/стрелки «наверх» в интерфейсе нет — это единственный
    // способ подняться по папке (решение design.md).
    override fun onBackPressed() {
        if (!goUp()) super.onBackPressed()
    }

    // Переход внутрь группы — вызывается адаптером при выборе строки-группы.
    private fun openGroup(node: LibraryNode) {
        nodeStack.addLast(node)
        updateHeader()
        loadFolder()
    }

    // Подъём на уровень выше. Возвращает false, если мы уже в корне (стек
    // пуст) — тогда действие недоступно, как того требует browsing/spec.md.
    private fun goUp(): Boolean {
        if (nodeStack.isEmpty()) return false
        nodeStack.removeLast()
        updateHeader()
        loadFolder()
        return true
    }

    // Заголовок в шапке экрана: имя текущей группы или обозначение корня
    // библиотеки, если стек пуст (browsing/spec.md, «Заголовок текущей папки»).
    private fun updateHeader() {
        headerText.text = nodeStack.lastOrNull()?.name ?: getString(R.string.library_root_title)
    }

    // Загружает содержимое текущей папки (id — вершина nodeStack, null — корень):
    // дерево групп и каталог файлов. Оба запроса асинхронные и независимые —
    // ждём оба, потом строим список. myGeneration защищает от гонки: если
    // пользователь успел перейти в другую папку, пока эти ответы летели по
    // сети, устаревший результат просто игнорируется (см. loadGeneration).
    private fun loadFolder() {
        val myGeneration = ++loadGeneration
        showStatus(getString(R.string.library_loading))

        var tree: LibraryTree? = null
        var media: List<MediaCard>? = null
        var failureMessage: String? = null

        fun tryRender() {
            if (myGeneration != loadGeneration) return // устарело — уже открыта другая папка

            val currentTree = tree
            val currentMedia = media
            when {
                failureMessage != null -> showStatus(getString(R.string.library_error, failureMessage))
                currentTree != null && currentMedia != null ->
                    renderFolder(nodeStack.lastOrNull()?.id, currentTree, currentMedia)
            }
        }

        LibraryApi.getTree(libraryUrl) { result ->
            result.onSuccess { tree = it }
            result.onFailure { failureMessage = it.message ?: it.toString() }
            tryRender()
        }
        LibraryApi.getMedia(libraryUrl) { result ->
            result.onSuccess { media = it }
            result.onFailure { failureMessage = it.message ?: it.toString() }
            tryRender()
        }
    }

    // Строит список текущей папки (nodeId — id её узла, null — корень):
    // - группы: дочерние узлы этого узла (у корня — узлы без родителя);
    // - файлы: в корне — файлы, не состоящие ни в одной группе; внутри
    //   группы — файлы её членства. Карточка файла берётся из каталога, если
    //   он сейчас доступен; иначе (present=false) используем заглушку с id
    //   вместо названия — настоящая пометка «недоступен» появится на шаге 5.
    private fun renderFolder(nodeId: String?, tree: LibraryTree, media: List<MediaCard>) {
        val groups = tree.nodes
            .filter { it.parentId == nodeId }
            .sortedBy { it.name }
            .map { LibraryListItem.Group(it) }

        val mediaById = media.associateBy { it.id }
        val files = if (nodeId == null) {
            val groupedMediaIds = tree.items.map { it.mediaId }.toHashSet()
            media.filter { it.id !in groupedMediaIds }
        } else {
            tree.items
                .filter { it.nodeId == nodeId }
                .map { item -> mediaById[item.mediaId] ?: MediaCard(item.mediaId, item.mediaId, item.present) }
        }

        val items = groups + files.sortedBy { it.title }.map { LibraryListItem.File(it) }

        if (items.isEmpty()) {
            showStatus(getString(R.string.library_empty))
        } else {
            statusText.visibility = View.GONE
            list.visibility = View.VISIBLE
            list.adapter = LibraryListAdapter(items, ::openGroup)
        }
    }

    // Показывает текстовое сообщение (загрузка/ошибка/пусто) вместо списка.
    private fun showStatus(message: String) {
        list.visibility = View.GONE
        statusText.visibility = View.VISIBLE
        statusText.text = message
    }
}
