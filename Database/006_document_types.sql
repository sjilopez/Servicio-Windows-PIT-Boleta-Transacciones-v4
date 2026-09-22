ALTER TABLE ocr_results
    ADD COLUMN document_type VARCHAR(100) NOT NULL DEFAULT 'NO CLASIFICADO' AFTER matched_phrases_json;

CREATE INDEX ix_ocr_results_document_type
    ON ocr_results (document_type, processed_at);
