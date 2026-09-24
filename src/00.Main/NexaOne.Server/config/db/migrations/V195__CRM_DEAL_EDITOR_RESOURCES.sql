INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'crm.clientInvalid' AS RESOURCE_KEY, 'Client ID must be a valid GUID.' AS VALUE
    UNION ALL SELECT 'crm.create', 'Create'
    UNION ALL SELECT 'crm.dealDeleted', 'Deal deleted.'
    UNION ALL SELECT 'crm.dealEditor', 'Deal editor'
    UNION ALL SELECT 'crm.dealInputRequired', 'Check the deal title, 0–5 probability and stage.'
    UNION ALL SELECT 'crm.dealMoved', 'Deal stage moved.'
    UNION ALL SELECT 'crm.dealSaved', 'Deal saved.'
    UNION ALL SELECT 'crm.dealTitle', 'Deal title'
    UNION ALL SELECT 'crm.delete', 'Delete'
    UNION ALL SELECT 'crm.description', 'Description'
    UNION ALL SELECT 'crm.move', 'Move stage'
    UNION ALL SELECT 'crm.newDeal', 'New deal'
    UNION ALL SELECT 'crm.newPipeline', 'New pipeline'
    UNION ALL SELECT 'crm.pipelineDeleted', 'Pipeline deleted.'
    UNION ALL SELECT 'crm.pipelineEditor', 'Pipeline editor'
    UNION ALL SELECT 'crm.pipelineInputRequired', 'A pipeline name and at least one stage are required.'
    UNION ALL SELECT 'crm.pipelineSaved', 'Pipeline saved.'
    UNION ALL SELECT 'crm.save', 'Save'
    UNION ALL SELECT 'crm.selectPipelineFirst', 'Select a pipeline before choosing a deal stage.'
    UNION ALL SELECT 'crm.stageNames', 'Stage names (comma separated)'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');
