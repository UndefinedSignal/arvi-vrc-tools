# [ArVi] Parameter Driver Picker

Добавляет над инспектором **VRC Avatar Parameter Driver** блок **«Параметры (поиск)»**: для каждой записи драйвера параметр выбирается из плоского списка с полем поиска вместо огромного нативного попапа со всеми параметрами контроллера. Поиск по словам через пробел, тип параметра тоже участвует — `float hair`, `bool bite`.

- Destination для Set / Add / Random, Source и Destination для Copy.
- Рядом с каждым параметром в списке виден его тип, текущий отмечен точкой.
- Параметры, которых нет в контроллере, подсвечиваются.
- Инспектор VRChat SDK остаётся под блоком без изменений: Add / Up / Down / Delete, значения, диапазоны — всё там же.

Требует VRChat Avatars SDK (`com.vrchat.avatars`).

Часть пака [ArVi VRC Tools](https://github.com/UndefinedSignal/arvi-vrc-tools).
