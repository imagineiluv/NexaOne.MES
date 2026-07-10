-- P3-14 다국어 보강(2026-07-10) — MRP/CRP/사용통계/FDC 충실화 아크 신설 표면의 EnUs 리소스.
-- 규약(V071): 한국어가 기본(코드 인라인 폴백)이라 비-한국어만 시드. MENU_ID는 공통 문구 규약 'COMMON'.
-- 커버: dev 메뉴 잎 7(menu.*) + 신설 화면 제목 9(screen.*.title) + 신설 공통 UI 문구 7(grid.*/common.*).
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE) VALUES
    ('menu.NX_DEV_MRP',      'COMMON', 'EnUs', 'Material Requirements Planning (MRP)'),
    ('menu.NX_DEV_UOM',      'COMMON', 'EnUs', 'Unit of Measure (UOM)'),
    ('menu.NX_DEV_ITEMPLN',  'COMMON', 'EnUs', 'Item Planning Parameters'),
    ('menu.NX_DEV_MENUUSE',  'COMMON', 'EnUs', 'Menu Usage Statistics'),
    ('menu.NX_DEV_CRP',      'COMMON', 'EnUs', 'Capacity Requirements (CRP) Load'),
    ('menu.NX_DEV_WC',       'COMMON', 'EnUs', 'Work Center Management'),
    ('menu.NX_DEV_RTSTEP',   'COMMON', 'EnUs', 'Routing Step Management'),
    ('screen.NX_MRP_PLANNING.title',                    'COMMON', 'EnUs', 'Material Requirements Planning (MRP)'),
    ('screen.FACTORY_STD_UOM.title',                    'COMMON', 'EnUs', 'Unit of Measure (UOM) Management'),
    ('screen.FACTORY_STD_ITEM_PLANNING.title',          'COMMON', 'EnUs', 'Item Planning Parameters'),
    ('screen.SYS_MENU_USAGE_STATS.title',               'COMMON', 'EnUs', 'Menu Usage Statistics'),
    ('screen.FACTORY_STD_WORK_CENTER.title',            'COMMON', 'EnUs', 'Work Center Management'),
    ('screen.FACTORY_STD_ROUTING_STEP.title',           'COMMON', 'EnUs', 'Routing Step Management'),
    ('screen.NX_CRP_LOAD.title',                        'COMMON', 'EnUs', 'Capacity Requirements (CRP) - Work Center Load'),
    ('screen.EES_FDC_REAL_TIME_USER_MONITORING.title',  'COMMON', 'EnUs', 'FDC Real-Time Monitoring by User'),
    ('screen.EES_FDC_TOOL_TO_TOOL_MATCHING.title',      'COMMON', 'EnUs', 'FDC Tool-to-Tool Matching'),
    ('screen.SYS_USER_REQUESTS.title',                  'COMMON', 'EnUs', 'User Registration Approval'),
    ('grid.copied',              'COMMON', 'EnUs', 'Copied'),
    ('grid.copyFail',            'COMMON', 'EnUs', 'Copy failed - clipboard unavailable (HTTPS only)'),
    ('grid.rowsCopied',          'COMMON', 'EnUs', ' row(s) copied'),
    ('grid.exportFail',          'COMMON', 'EnUs', 'CSV export failed'),
    ('grid.persistFail',         'COMMON', 'EnUs', 'Personalization not saved - this session only'),
    ('common.more',              'COMMON', 'EnUs', 'More'),
    ('common.bridgeBulkUnwired', 'COMMON', 'EnUs', 'This command is only available on its dedicated page.');
