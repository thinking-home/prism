package org.prism.library

import android.os.Bundle
import android.util.TypedValue
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.TextView
import androidx.fragment.app.FragmentManager
import com.google.android.material.bottomsheet.BottomSheetDialogFragment

// Шторка выбора действия с файлом (шаг 7): открывается вместо прежнего
// прямого перехода в шторку «информация» (см. LibraryListAdapter.onFileClick
// в MainActivity) — теперь у файла два действия, и нужно место, где выбрать
// одно из них, не загромождая саму строку списка. Действие «включить на
// плеере» показывается только для файлов, которые реально можно запустить
// (media-actions/spec.md, «Ограничение действия «включить на плеере»
// неиграбельными файлами»); «информация» доступна всегда.
class FileActionsBottomSheet : BottomSheetDialogFragment() {

    companion object {
        private const val ARG_BASE_URL = "baseUrl"
        private const val ARG_MEDIA_ID = "mediaId"
        private const val ARG_CAN_PLAY = "canPlay"

        // Фабричный метод — см. пояснение в MediaInfoBottomSheet.show о том,
        // почему не обычный конструктор с аргументами.
        fun show(fragmentManager: FragmentManager, baseUrl: String, media: MediaCard) {
            val sheet = FileActionsBottomSheet().apply {
                arguments = Bundle().apply {
                    putString(ARG_BASE_URL, baseUrl)
                    putString(ARG_MEDIA_ID, media.id)
                    putBoolean(ARG_CAN_PLAY, media.present && media.playable)
                }
            }
            sheet.show(fragmentManager, "file-actions")
        }
    }

    // Та же тёмная тема, что и у шторки «информация» (design.md, шаг 6/9).
    override fun getTheme(): Int = R.style.AppBottomSheetDialogTheme

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        val baseUrl = requireArguments().getString(ARG_BASE_URL, "")
        val mediaId = requireArguments().getString(ARG_MEDIA_ID, "")
        val canPlay = requireArguments().getBoolean(ARG_CAN_PLAY)

        val root = LinearLayout(requireContext()).apply { orientation = LinearLayout.VERTICAL }

        root.addView(actionRow(getString(R.string.file_action_info)) {
            dismiss()
            MediaInfoBottomSheet.show(parentFragmentManager, baseUrl, mediaId)
        })

        if (canPlay) {
            root.addView(actionRow(getString(R.string.file_action_play)) {
                dismiss()
                PlayerListBottomSheet.show(parentFragmentManager, baseUrl, mediaId)
            })
        }

        return root
    }

    // Одна строка действия — тот же стиль строки, что и в LibraryListAdapter
    // (текст с отступами, подсветка при фокусе/нажатии, доступность для D-pad).
    private fun actionRow(text: String, onClick: () -> Unit): TextView {
        val pad = (16 * resources.displayMetrics.density).toInt()
        return TextView(requireContext()).apply {
            this.text = text
            textSize = 18f
            setPadding(pad, pad, pad, pad)
            isFocusable = true
            val bg = TypedValue()
            context.theme.resolveAttribute(android.R.attr.selectableItemBackground, bg, true)
            setBackgroundResource(bg.resourceId)
            setOnClickListener { onClick() }
        }
    }
}
