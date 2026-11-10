# ממצאי בדיקה (2026-11-10)

נאספו בקריאה בלבד, בלי שינוי הגדרות.

## מה הלוח טוען
מהיסטוריית ה-WebView2 של `MicrosoftWindows.Client.WebExperience`:

`https://windows.msn.com/app.html?locale=he-IL&region=US&aver=...&over=...&devicetype=10&oem=...&machineId=...&clv=1.0&...`

קובצי ה-JS של הלוח (`winWidgets`) נטענים מ-`https://assets.msn.com/bundles/v1/winWidgets/latest/`.

## מצב הדומיינים תחת NetFree

| דומיין | תוצאה | הערה |
|---|---|---|
| `windows.msn.com` | 307 ל-`//www.google.com/search?q=` | **זה מה שגורם לשגיאת הלוח** |
| `ntp.msn.com` | 418 (חסום) | |
| `assets.msn.com` | 404 בשורש (נגיש) | |
| `api.msn.com` | 418 בשורש; נתיבי weather מחזירים 401 | |
| `www.msn.com/he-il/weather` | 200 | |
| `api.open-meteo.com` | 200 | |
| `hagizra.news/api/messages` | 200 | מקור החדשות |

ב-DNS, `windows.msn.com`, `assets.msn.com` ו-`ntp.msn.com` מתורגמים לכתובות 147.235.21.x (NetFree); `api.msn.com` לכתובות של מיקרוסופט.

## סביבה
- אין פרוקסי מוגדר במערכת (לא ב-WinINET ולא ב-WinHTTP): הסינון ברמת הרשת, ולכן הפניה דרך hosts צפויה לעבוד.
- שורשי NetFree מותקנים ב-`LocalMachine\Root`.
- בקובץ hosts יש כבר רשומות מסוג זה (גוגל, יוטיוב, בינג).
- פורטים 80 ו-443 פנויים.

## מקור החדשות
`https://hagizra.news/api/messages?limit=N&offset=0` מחזיר מערך JSON של הודעות
(`id`, `text` בפורמט markdown עם סימוני `[image-embedded#](...)`, `timestamp`, `breakingId`, `deleted`, `is_ads`...).
הלוגיקה הועתקה מ-`hagizra-widget` (`HagizraApi.cs`, `NewsNormalizer.cs`).
