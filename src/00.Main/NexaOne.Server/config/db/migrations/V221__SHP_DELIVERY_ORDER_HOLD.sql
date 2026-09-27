-- A delivery order may be held before shipment; existing orders remain unheld.
ALTER TABLE SHP_DELIVERY_ORDER ADD IS_HOLD CHAR(1) NOT NULL DEFAULT 'N';
