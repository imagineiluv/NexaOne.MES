-- P3-14 다국어 — 전역 API 토스트(Radzen 알림) 심각도 제목 EnUs 리소스 시드.
-- 규약: 한국어가 기본(코드 인라인 폴백)이라 비-한국어만 시드한다. MENU_ID='COMMON'.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE) VALUES
    ('toast.error',   'COMMON', 'EnUs', 'Error'),
    ('toast.warning', 'COMMON', 'EnUs', 'Warning'),
    ('toast.info',    'COMMON', 'EnUs', 'Notice');
