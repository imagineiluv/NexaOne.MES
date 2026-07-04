-- P3-14 서버 오류 메시지 다국어 — 서버 모듈 Error.MessageKey의 EnUs 리소스 시드.
-- 규약: 한국어가 기본(Error.Description 인라인 폴백)이라 비-한국어만 시드한다. MENU_ID='COMMON'.
-- error.notFound: {0}=엔티티명, {1}=식별자. Error.NotFoundOf(entity, id)가 이 키+인자를 실어 보낸다.
-- 응답 경계 필터(ErrorLocalizationFilter)가 Accept-Language=en-US 요청에서 Description을 이 값으로 치환한다.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE) VALUES
    ('error.notFound', 'COMMON', 'EnUs', '{0} ''{1}'' was not found.');
