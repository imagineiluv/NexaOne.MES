-- P3-14 다국어 — 파괴적 명령 확인 다이얼로그(RadzenDialog) 문구 EnUs 리소스 시드.
-- 규약: 한국어가 기본(코드 인라인 폴백)이라 비-한국어만 시드한다. MENU_ID='COMMON'.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE) VALUES
    ('common.confirmTitle', 'COMMON', 'EnUs', 'Confirm'),
    ('common.confirm',      'COMMON', 'EnUs', 'OK'),
    ('common.cancel',       'COMMON', 'EnUs', 'Cancel');
