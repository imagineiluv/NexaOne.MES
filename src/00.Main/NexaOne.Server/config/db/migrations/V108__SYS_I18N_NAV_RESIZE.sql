-- Sidebar resize accessibility resources (2026-07-22).
-- These labels are visible to assistive technology and must switch with the rest of the shell.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE) VALUES
    ('shell.resizeNavigation',
     'COMMON', 'EnUs', 'Resize navigation'),
    ('shell.resizeNavigationHint',
     'COMMON', 'EnUs', 'Drag or use the left and right arrow keys to resize');
