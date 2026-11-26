# EfMailAgent kiadási megjegyzések

## 1.0.0 (2026-11-26)

Az első teljes kiadás.

### Letölthető fájlok

| Fájl | Mire való |
|---|---|
| `EfMailAgent-Setup.exe` | Telepítő a Windows szerverre (szolgáltatás + Thunderbird bővítmény). Ezt kell futtatni. |
| `EfMailAgent.exe` | Önálló program (telepítő nélküli, kézi használatra) |
| `efmailagent.xpi` | A Thunderbird bővítmény külön (a telepítő ezt magától felteszi) |
| `SHA256SUMS.txt` | Ellenőrző összegek |

### Mit tud

- A továbbított, szabad szöveges előrendelő e-mailekből helyi AI-val (Ollama / LM Studio) CSV sorokat készít; a levelek
  nem hagyják el a szervert.
- Thunderbird bővítmény: a tárgy kulcsszavai alapján beküldi a leveleket, címkével jelzi az állapotukat
  (AI: sorban / kész / ellenőrzendő / hiba), pótolja a kimaradt leveleket, kézi beküldés jobb gombbal.
- Szabályok YAML + Markdown fájlokban, újraindítás nélkül szerkeszthetők; új levéltípus felvehető kód nélkül.
- Admin felület a böngészőben (`http://127.0.0.1:47811/admin`): áttekintés, szabályszerkesztő, Tesztkonzol, beállítások,
  napló.
- Megbízható sorkezelés: egyszerre egy levél, újraindítás és AI-kiesés után folytatja, zárolt CSV esetén nem veszít sort.
- Magyar kezelési útmutató: `USER_GUIDE_HU.md` (a programmappában).

### Ellenőrizve a kiadás előtt

- 292 C# és 85 add-on teszt, végponttól végpontig szimuláció.
- Admin felület használati teszt (39 lépés, böngészőben): Linuxon és a telepített Windows szolgáltatáson is.
- Telepítő Windowson: telepítés paraméterekkel, a Thunderbird bővítmény automatikus telepítése, frissítés (beállítások,
  szabályok, CSV megmaradnak), eltávolítás (az adatok megmaradnak).

### Ismert korlátok

- A Thunderbirdnek egy bejelentkezett munkamenetben futnia kell.
- A valódi AI modellel, Thunderbirddel és hálózati megosztással való próba a szerveren végzendő
  (`docs/WINDOWS_ACCEPTANCE.md`).
- A program és a bővítmény nincs digitálisan aláírva (a SmartScreen figyelmeztethet).
