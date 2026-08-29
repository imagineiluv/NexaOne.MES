-- SLS display terminology normalization (2026-07-16).
-- Keep stable SLS query/table/field identifiers; only align user-facing menu, screen and i18n labels.
-- Exact legacy-value guards preserve labels customized through menu management or Designer.

-- The common management editor was introduced with this release. INSERT...WHERE NOT EXISTS makes the
-- migration safe after V099 on fresh databases while backfilling databases that already applied V099.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'common.editorNew', 'COMMON', 'EnUs', 'New entry'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'common.editorNew' AND LANGUAGE = 'EnUs');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'common.editorEdit', 'COMMON', 'EnUs', 'Editing'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'common.editorEdit' AND LANGUAGE = 'EnUs');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'common.editorNewHint', 'COMMON', 'EnUs', 'Enter the required information and save a new record.'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'common.editorNewHint' AND LANGUAGE = 'EnUs');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'common.editorEditHint', 'COMMON', 'EnUs', 'Review the selected record and save your changes.'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'common.editorEditHint' AND LANGUAGE = 'EnUs');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'common.resetInput', 'COMMON', 'EnUs', 'Reset form'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'common.resetInput' AND LANGUAGE = 'EnUs');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'common.cancelEdit', 'COMMON', 'EnUs', 'Cancel edit'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'common.cancelEdit' AND LANGUAGE = 'EnUs');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'common.saveNew', 'COMMON', 'EnUs', 'Save new'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'common.saveNew' AND LANGUAGE = 'EnUs');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'common.saveChanges', 'COMMON', 'EnUs', 'Save changes'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'common.saveChanges' AND LANGUAGE = 'EnUs');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'status.producing', 'COMMON', 'EnUs', 'Producing'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'status.producing' AND LANGUAGE = 'EnUs');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'status.delivered', 'COMMON', 'EnUs', 'Delivered'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'status.delivered' AND LANGUAGE = 'EnUs');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'status.closed', 'COMMON', 'EnUs', 'Closed'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY = 'status.closed' AND LANGUAGE = 'EnUs');

UPDATE SYS_MENU
   SET MENU_NAME = '수주 관리'
 WHERE MENU_ID = 'FACTORY_SLS_SALES_ORDER'
   AND MENU_NAME = '수주관리';

UPDATE SYS_MENU
   SET MENU_NAME = '판매 요청'
 WHERE MENU_ID = 'FACTORY_SLS_SALES_REQUEST'
   AND MENU_NAME = '판매 주문 접수';

UPDATE SYS_MULTI_LANGUAGE_RESOURCE
   SET VALUE = 'Sales Request'
 WHERE RESOURCE_KEY = 'menu.FACTORY_SLS_SALES_REQUEST'
   AND LANGUAGE = 'EnUs'
   AND VALUE = 'Sales Order Receipt';

UPDATE SYS_MULTI_LANGUAGE_RESOURCE
   SET VALUE = 'Shipping Status'
 WHERE RESOURCE_KEY = 'screen.FACTORY_SLS_REPORT_DELIVERY.title'
   AND LANGUAGE = 'EnUs'
   AND VALUE = 'Delivery Status';

-- Imported code seeds are DB-first at runtime. Update both the catalog title and the JSON title, handling
-- Designer JSON (literal Korean) and System.Text.Json seed JSON (\uXXXX escaped Korean).
UPDATE SYS_SCREEN_DEFINITION
   SET TITLE = '수주 관리',
       DEFINITION_JSON = REPLACE(
           REPLACE(DEFINITION_JSON,
               '"판매 오더 관리"', '"수주 관리"'),
               '"\uD310\uB9E4 \uC624\uB354 \uAD00\uB9AC"', '"\uC218\uC8FC \uAD00\uB9AC"')
 WHERE UI_ID = 'FACTORY_SLS_SALES_ORDER'
   AND TITLE = '판매 오더 관리';

UPDATE SYS_SCREEN_DEFINITION
   SET TITLE = '출하 현황',
       DEFINITION_JSON = REPLACE(
           REPLACE(DEFINITION_JSON,
               '"납품 현황"', '"출하 현황"'),
               '"\uB0A9\uD488 \uD604\uD669"', '"\uCD9C\uD558 \uD604\uD669"')
 WHERE UI_ID = 'FACTORY_SLS_REPORT_DELIVERY'
   AND TITLE = '납품 현황';

-- Normalize untouched field/column labels inside imported definitions. The UI_ID boundary plus quoted exact
-- JSON values avoids changing stable keys or labels customized to any other text.
UPDATE SYS_SCREEN_DEFINITION
   SET DEFINITION_JSON = REPLACE(
           REPLACE(DEFINITION_JSON,
               '"판매오더 번호"', '"수주 번호"'),
               '"\uD310\uB9E4\uC624\uB354 \uBC88\uD638"', '"\uC218\uC8FC \uBC88\uD638"')
 WHERE UI_ID = 'FACTORY_SLS_SALES_ORDER';

UPDATE SYS_SCREEN_DEFINITION
   SET DEFINITION_JSON = REPLACE(
           REPLACE(DEFINITION_JSON,
               '"판매오더명"', '"수주명"'),
               '"\uD310\uB9E4\uC624\uB354\uBA85"', '"\uC218\uC8FC\uBA85"')
 WHERE UI_ID = 'FACTORY_SLS_SALES_ORDER';

UPDATE SYS_SCREEN_DEFINITION
   SET DEFINITION_JSON = REPLACE(
           REPLACE(DEFINITION_JSON,
               '"판매오더 ID"', '"수주 번호"'),
               '"\uD310\uB9E4\uC624\uB354 ID"', '"\uC218\uC8FC \uBC88\uD638"')
 WHERE UI_ID = 'FACTORY_SLS_SALES_ORDER';

UPDATE SYS_SCREEN_DEFINITION
   SET DEFINITION_JSON = REPLACE(
           REPLACE(DEFINITION_JSON,
               '"판매오더"', '"수주 번호"'),
               '"\uD310\uB9E4\uC624\uB354"', '"\uC218\uC8FC \uBC88\uD638"')
 WHERE UI_ID = 'FACTORY_SLS_SALES_REQUEST';
