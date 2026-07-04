-- P3-14 v3 다국어 — 클라이언트 대면 메시지(검증·저장 실패·빈 상태) EnUs 리소스 시드.
-- 규약: 한국어가 기본(코드 인라인 폴백)이라 비-한국어만 시드한다. MENU_ID는 공통 문구 규약대로 'COMMON'.
-- 검증 메시지(common.requiredField)는 {0}=필드 라벨 자리표시자를 유지한다(클라이언트 string.Format 조립).
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE) VALUES
    ('common.requiredField',    'COMMON', 'EnUs', '{0} is required.'),
    ('common.saveFailed',       'COMMON', 'EnUs', 'Save failed (check permission/input).'),
    ('common.noSaveQuery',      'COMMON', 'EnUs', 'No save (write) query is bound to this screen.'),
    ('common.screenPending',    'COMMON', 'EnUs', 'Screen in preparation'),
    ('common.notMigratedTitle', 'COMMON', 'EnUs', 'This screen has not been migrated yet.'),
    ('common.notMigratedId',    'COMMON', 'EnUs', 'Screen identifier carried over from the SmartUX menu:'),
    ('common.notMigratedHint',  'COMMON', 'EnUs', 'This screen is provided in the order of module backend migration.');
