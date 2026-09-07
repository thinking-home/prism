package org.prism.library

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.TextView
import androidx.fragment.app.FragmentManager
import com.google.android.material.bottomsheet.BottomSheetDialogFragment

// Шторка «информация о файле» (шаг 6): выезжает снизу поверх текущего списка
// папки, который остаётся открытым под ней (media-actions/spec.md, «Открытие
// шторки с информацией»). Открывается кликом по строке файла (MainActivity).
// Разметка — тот же стиль, что и весь остальной интерфейс: View создаются
// кодом, без XML.
class MediaInfoBottomSheet : BottomSheetDialogFragment() {

    companion object {
        private const val ARG_BASE_URL = "baseUrl"
        private const val ARG_MEDIA_ID = "mediaId"

        // Фабричный метод вместо публичного конструктора с аргументами —
        // системе иногда нужно пересоздать фрагмент самой (например, после
        // поворота экрана), обязательно через конструктор без параметров;
        // данные она берёт из arguments, которые мы кладём здесь.
        fun show(fragmentManager: FragmentManager, baseUrl: String, mediaId: String) {
            val sheet = MediaInfoBottomSheet().apply {
                arguments = Bundle().apply {
                    putString(ARG_BASE_URL, baseUrl)
                    putString(ARG_MEDIA_ID, mediaId)
                }
            }
            sheet.show(fragmentManager, "media-info")
        }
    }

    // Своя тёмная тема шторки вместо светлой по умолчанию у Material
    // Components (design.md, замечание пользователя на ревью шага 6): без
    // неё фон шторки был белым, а текст (унаследованный от тёмной AppTheme)
    // светлым — нечитаемая комбинация.
    override fun getTheme(): Int = R.style.AppBottomSheetDialogTheme

    // Текст состояния (загрузка/ошибка/недоступен) — заменяется карточкой при
    // успешной загрузке.
    private lateinit var statusText: TextView

    // Карточка с данными файла — показывается вместо statusText при успехе.
    private lateinit var detailText: TextView

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        val pad = (16 * resources.displayMetrics.density).toInt()

        statusText = TextView(requireContext()).apply { textSize = 16f }
        detailText = TextView(requireContext()).apply {
            textSize = 16f
            visibility = View.GONE
        }

        return LinearLayout(requireContext()).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
            addView(statusText)
            addView(detailText)
        }
    }

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)

        val baseUrl = requireArguments().getString(ARG_BASE_URL, "")
        val mediaId = requireArguments().getString(ARG_MEDIA_ID, "")

        statusText.text = getString(R.string.media_info_loading)

        LibraryApi.getMediaDetail(baseUrl, mediaId) { result ->
            // isAdded — шторку могли уже закрыть, пока ответ летел по сети;
            // тогда её View больше не существует, и обновлять в нём нечего.
            if (!isAdded) return@getMediaDetail

            result.onSuccess { showDetail(it) }
            result.onFailure { error ->
                // 404 — файл сейчас недоступен ни на одном хосте: отдельное
                // сообщение вместо общей ошибки (media-actions/spec.md, «Файл
                // недоступен при запросе информации»).
                val message = if (error is HttpStatusException && error.code == 404) {
                    getString(R.string.media_info_unavailable)
                } else {
                    getString(R.string.library_error, error.message ?: error.toString())
                }
                statusText.text = message
            }
        }
    }

    // Заполняет карточку данными файла и показывает её вместо статуса.
    private fun showDetail(detail: MediaDetail) {
        statusText.visibility = View.GONE
        detailText.visibility = View.VISIBLE

        val minutes = (detail.durationSeconds / 60).toInt()
        val seconds = (detail.durationSeconds % 60).toInt()
        val duration = "%d:%02d".format(minutes, seconds)

        // Каждая характеристика — своя строка; codec/host не всегда известны
        // серверу (null) — такую строку просто не показываем.
        val lines = mutableListOf(
            detail.title,
            getString(R.string.media_info_duration, duration),
            getString(R.string.media_info_resolution, detail.width, detail.height),
        )
        detail.videoCodec?.let { lines += getString(R.string.media_info_video_codec, it) }
        detail.audioCodec?.let { lines += getString(R.string.media_info_audio_codec, it) }
        detail.host?.let { lines += getString(R.string.media_info_host, it) }

        detailText.text = lines.joinToString("\n")
    }
}
