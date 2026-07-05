-- P3-14 다국어 — 페이저 버튼이 RadzenIcon(chevron_left/right)으로 방향 표시를 갖게 되어,
-- 텍스트의 화살표(‹ ›)를 제거한다(아이콘+텍스트 이중 화살표 방지). 한국어 폴백은 코드에서 이미 '이전/다음'.
UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE = 'Prev' WHERE RESOURCE_KEY = 'common.prev' AND LANGUAGE = 'EnUs';
UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE = 'Next' WHERE RESOURCE_KEY = 'common.next' AND LANGUAGE = 'EnUs';
