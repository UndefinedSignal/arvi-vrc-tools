# ArVi VRC Tools

Пак самописных редакторских тулов для Unity / VRChat. Каждый тул — отдельный VPM-пакет, все ставятся из одного репозитория в VRChat Creator Companion.

| Пакет | Что делает |
|---|---|
| [`com.arvi.blendtree-param-picker`](Packages/com.arvi.blendtree-param-picker) | **[ArVi] Blend Tree Param Picker** — поиск по Float-параметрам в инспекторе Blend Tree вместо вложенных подменю |
| [`com.arvi.parameter-driver-picker`](Packages/com.arvi.parameter-driver-picker) | **[ArVi] Parameter Driver Picker** — поиск по параметрам в инспекторе VRC Avatar Parameter Driver (нужен VRChat Avatars SDK) |
| [`com.arvi.clip-browser`](Packages/com.arvi.clip-browser) | **[ArVi] Clip Browser** — переключатель клипов для окна Animation: группы, превью, избранное, хоткеи |

## Установка через VCC

1. Открой https://undefinedsignal.github.io/arvi-vrc-tools/ и нажми **Add to VCC**
   — или в VCC: **Settings → Packages → Add Repository** и вставь
   `https://undefinedsignal.github.io/arvi-vrc-tools/index.json`
2. В проекте: **Manage Project** → включи нужные тулы из **ArVi VRC Tools**.

Если раньше тулы лежали в `Assets` вручную — удали старые копии, чтобы не было двух одинаковых инспекторов и хоткеев (VCC сам уберёт `Assets/Editor/ClipBrowser` и `Assets/Editor/BlendTreeParamPicker.cs`).

## Ручная установка

Скачай zip нужного пакета из [Releases](https://github.com/UndefinedSignal/arvi-vrc-tools/releases) и распакуй в папку `Packages/<имя пакета>/` проекта.

## Для разработки

```
Packages/<имя пакета>/
  package.json        ← версия и описание пакета
  CHANGELOG.md
  Editor/
    *.asmdef          ← editor-only сборка
    *.cs
```

**Выпустить новую версию:** подними `version` в `package.json` пакета, допиши `CHANGELOG.md`, запушь в `main`. GitHub Actions соберёт zip, создаст релиз `<пакет>-<версия>` и обновит листинг для VCC. Уже выпущенные версии не пересобираются.

**Добавить новый тул:** создай `Packages/com.arvi.<имя>/` по образцу существующих (package.json, Editor/*.asmdef), затем

```
python .github/scripts/vpm.py meta    # создаст .meta со стабильными GUID
python .github/scripts/vpm.py pack    # локальная проверка: zip-ы и index.json в dist/
```

### Однократная настройка GitHub

**Settings → Pages → Build and deployment → Source: GitHub Actions.** После этого первый пуш в `main` (или ручной запуск workflow во вкладке Actions) выпустит релизы и опубликует листинг.

## Лицензия

MIT — см. [LICENSE](LICENSE).
