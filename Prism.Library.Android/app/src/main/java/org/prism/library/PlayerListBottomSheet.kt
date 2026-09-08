package org.prism.library

import android.os.Bundle
import android.util.TypedValue
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.fragment.app.FragmentManager
import com.google.android.material.bottomsheet.BottomSheetDialogFragment

// Шторка «включить на плеере» (шаг 7): список плееров, известных библиотеке
// (GET /api/players), выбор одного из них отправляет команду воспроизведения
// (POST /api/players/{id}/open). Открывается из FileActionsBottomSheet.
// Простой список без «последнего использованного» и автовыбора — осознанное
// решение design.md («Список плееров и выбор при запуске»).
class PlayerListBottomSheet : BottomSheetDialogFragment() {

    companion object {
        private const val ARG_BASE_URL = "baseUrl"
        private const val ARG_MEDIA_ID = "mediaId"

        fun show(fragmentManager: FragmentManager, baseUrl: String, mediaId: String) {
            val sheet = PlayerListBottomSheet().apply {
                arguments = Bundle().apply {
                    putString(ARG_BASE_URL, baseUrl)
                    putString(ARG_MEDIA_ID, mediaId)
                }
            }
            sheet.show(fragmentManager, "player-list")
        }
    }

    override fun getTheme(): Int = R.style.AppBottomSheetDialogTheme

    // Текст состояния (загрузка/ошибка/пусто/отправка команды) — заменяется
    // списком плееров при успешной загрузке.
    private lateinit var statusText: TextView

    // Контейнер строк-плееров — заполняется после загрузки списка.
    private lateinit var playerList: LinearLayout

    private var baseUrl: String = ""
    private var mediaId: String = ""

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        val pad = (16 * resources.displayMetrics.density).toInt()

        statusText = TextView(requireContext()).apply {
            textSize = 16f
            setPadding(pad, pad, pad, pad)
        }
        playerList = LinearLayout(requireContext()).apply { orientation = LinearLayout.VERTICAL }

        return LinearLayout(requireContext()).apply {
            orientation = LinearLayout.VERTICAL
            addView(statusText)
            addView(playerList)
        }
    }

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)

        baseUrl = requireArguments().getString(ARG_BASE_URL, "")
        mediaId = requireArguments().getString(ARG_MEDIA_ID, "")

        statusText.text = getString(R.string.players_loading)

        LibraryApi.getPlayers(baseUrl) { result ->
            // Шторку могли закрыть, пока ответ летел по сети — тогда её View
            // уже не существует (см. тот же приём в MediaInfoBottomSheet).
            if (!isAdded) return@getPlayers

            result.onSuccess { players -> showPlayers(players) }
            result.onFailure { error ->
                // 503 — MQTT-брокер не настроен в библиотеке (media-actions/spec.md,
                // «MQTT не настроен в библиотеке») — отдельное сообщение вместо
                // общей ошибки, как и у шторки «информация» для 404.
                statusText.text = if (error is HttpStatusException && error.code == 503) {
                    getString(R.string.players_unavailable)
                } else {
                    getString(R.string.library_error, error.message ?: error.toString())
                }
            }
        }
    }

    // Показывает список плееров или сообщение «плееры не найдены», если
    // список пуст (media-actions/spec.md, «Нет известных плееров»).
    private fun showPlayers(players: List<Player>) {
        if (players.isEmpty()) {
            statusText.text = getString(R.string.players_empty)
            return
        }

        statusText.visibility = View.GONE
        val pad = (16 * resources.displayMetrics.density).toInt()
        players.forEach { player ->
            val row = TextView(requireContext()).apply {
                // Статус online — часть ответа сервера, показываем рядом с
                // именем, чтобы пользователь мог сразу отличить включённый
                // плеер от выключенного, не открывая ничего дополнительно.
                text = if (player.online) {
                    player.name
                } else {
                    getString(R.string.player_offline, player.name)
                }
                textSize = 18f
                setPadding(pad, pad, pad, pad)
                isFocusable = true
                val bg = TypedValue()
                context.theme.resolveAttribute(android.R.attr.selectableItemBackground, bg, true)
                setBackgroundResource(bg.resourceId)
                setOnClickListener { openOnPlayer(player) }
            }
            playerList.addView(row)
        }
    }

    // Отправляет команду воспроизведения выбранному плееру и показывает
    // результат (media-actions/spec.md, «Выбор плеера и запуск»). Результат —
    // Toast, а не текст в шторке: шторка сразу закрывается, чтобы не держать
    // пользователя на экране, для которого больше нет действий.
    private fun openOnPlayer(player: Player) {
        playerList.visibility = View.GONE
        statusText.visibility = View.VISIBLE
        statusText.text = getString(R.string.players_sending)

        LibraryApi.openOnPlayer(baseUrl, player.id, mediaId) { result ->
            if (!isAdded) return@openOnPlayer

            val message = result.fold(
                onSuccess = { getString(R.string.players_open_success) },
                onFailure = { error ->
                    when {
                        error is HttpStatusException && error.code == 400 ->
                            getString(R.string.players_open_not_playable)
                        error is HttpStatusException && error.code == 404 ->
                            getString(R.string.players_open_not_found)
                        error is HttpStatusException && error.code == 503 ->
                            getString(R.string.players_open_broker_unavailable)
                        else -> getString(R.string.library_error, error.message ?: error.toString())
                    }
                },
            )
            Toast.makeText(requireContext(), message, Toast.LENGTH_LONG).show()
            dismiss()
        }
    }
}
