-- P3-14 다국어 — 사이드바 내비 IA 구역(5구역) 헤더 EnUs 리소스 시드.
-- 규약: 한국어가 기본(코드 인라인 폴백)이라 비-한국어만 시드한다. MENU_ID='COMMON'.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE) VALUES
    ('nav.section.ops',     'COMMON', 'EnUs', 'Operations'),
    ('nav.section.quality', 'COMMON', 'EnUs', 'Quality'),
    ('nav.section.equip',   'COMMON', 'EnUs', 'Equipment'),
    ('nav.section.master',  'COMMON', 'EnUs', 'Master Data'),
    ('nav.section.system',  'COMMON', 'EnUs', 'System');
