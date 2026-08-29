-- Distinguish an unbound work order, a single-operation work order, and one work order
-- that owns the complete serial product route. Existing route-step-bound rows retain
-- their operation semantics; all other legacy rows remain unbound.
ALTER TABLE POM_WORK_ORDER ADD ROUTING_SCOPE NVARCHAR(20) NOT NULL
    CONSTRAINT DF_POM_WORK_ORDER_ROUTING_SCOPE DEFAULT 'Unbound';

UPDATE POM_WORK_ORDER
   SET ROUTING_SCOPE = CASE
       WHEN ROUTING_ID IS NOT NULL AND LTRIM(RTRIM(ROUTING_ID)) <> ''
        AND ROUTING_STEP_NO IS NOT NULL
       THEN 'Operation'
       ELSE 'Unbound'
   END;

ALTER TABLE POM_WORK_ORDER DROP CONSTRAINT CK_POM_WORK_ORDER_ROUTING_BINDING;
ALTER TABLE POM_WORK_ORDER ADD CONSTRAINT CK_POM_WORK_ORDER_ROUTING_BINDING CHECK (
    (ROUTING_SCOPE = 'Unbound'
        AND ROUTING_ID IS NULL AND ROUTING_STEP_NO IS NULL)
    OR
    (ROUTING_SCOPE = 'Operation'
        AND ROUTING_ID IS NOT NULL AND LTRIM(RTRIM(ROUTING_ID)) <> ''
        AND ROUTING_STEP_NO > 0)
    OR
    (ROUTING_SCOPE = 'SerialRoute'
        AND ROUTING_ID IS NOT NULL AND LTRIM(RTRIM(ROUTING_ID)) <> ''
        AND ROUTING_STEP_NO IS NULL AND PROCESS_ID IS NULL)
);

-- A serial work order resolves its ordered execution processes from the product-routing
-- master. The column is nullable for legacy master rows; application validation requires
-- a process for every step before a SerialRoute work order can execute.
ALTER TABLE MDM_ROUTING_STEP ADD PROCESS_ID NVARCHAR(50) NULL;
ALTER TABLE MDM_ROUTING_STEP ADD CONSTRAINT FK_MDM_ROUTING_STEP_PROCESS
    FOREIGN KEY (PROCESS_ID) REFERENCES MDM_PROCESS (PROCESS_ID);

CREATE INDEX IX_MDM_ROUTING_STEP_PROCESS
    ON MDM_ROUTING_STEP (ROUTING_ID, STEP_NO, PROCESS_ID);
