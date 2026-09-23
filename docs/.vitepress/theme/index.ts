import { h } from 'vue'
import DefaultTheme from 'vitepress/theme'
import './style.css'

export default {
  extends: DefaultTheme,
  Layout: () => h(DefaultTheme.Layout, null, {
    'home-hero-actions-after': () => h('p', { class: 'tv-note' },
      'Для просмотра нужен Android TV — встроенный в телевизор или на приставке.')
  })
}
