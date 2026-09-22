CREATE TABLE IF NOT EXISTS ocr_results (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    result_id CHAR(36) NOT NULL,
    file_hash CHAR(64) NOT NULL,
    file_name VARCHAR(500) NOT NULL,
    file_path VARCHAR(2000) NOT NULL,
    page_count INT NOT NULL,
    line_count INT NOT NULL,
    full_text LONGTEXT NOT NULL,
    ocr_json JSON NOT NULL,
    matched_phrases_json JSON NOT NULL,
    match_count INT NOT NULL,
    is_transaction_receipt BOOLEAN NOT NULL,
    ocr_elapsed_ms BIGINT NOT NULL,
    processed_at DATETIME(3) NOT NULL,
    created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    PRIMARY KEY (id),
    UNIQUE KEY uk_ocr_results_file_hash (file_hash),
    UNIQUE KEY uk_ocr_results_result_id (result_id),
    KEY ix_ocr_results_classification (is_transaction_receipt, processed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ocr_retry_control (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    file_hash CHAR(64) NOT NULL,
    file_name VARCHAR(500) NOT NULL,
    file_path VARCHAR(2000) NOT NULL,
    first_attempt_at DATETIME(3) NOT NULL,
    last_attempt_at DATETIME(3) NOT NULL,
    next_attempt_at DATETIME(3) NOT NULL,
    attempt_count INT NOT NULL DEFAULT 0,
    status VARCHAR(30) NOT NULL,
    last_error TEXT NULL,
    updated_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
    PRIMARY KEY (id),
    UNIQUE KEY uk_ocr_retry_file_hash (file_hash),
    KEY ix_ocr_retry_next_attempt (status, next_attempt_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;