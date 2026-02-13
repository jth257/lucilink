-- =====================================================
-- expire_trials() 자동 실행을 위한 pg_cron 설정
-- Supabase Dashboard → SQL Editor에서 실행
-- =====================================================
-- 
-- 전제조건:
-- 1. Supabase Pro 플랜 이상 (pg_cron 확장 사용 가능)
-- 2. schema.sql의 expire_trials() 함수가 이미 생성되어 있어야 함
--
-- 이 마이그레이션은 매 시간 체험 만료 상태를 자동으로 전환합니다.
-- trial_end_date가 현재 시각을 지난 구독은 status='expired'로 변경됩니다.
-- =====================================================

-- pg_cron 확장 활성화 (이미 활성화된 경우 무시됨)
CREATE EXTENSION IF NOT EXISTS pg_cron;

-- 기존 스케줄이 있으면 삭제 (멱등성 보장)
SELECT cron.unschedule('expire-trials-job')
WHERE EXISTS (
  SELECT 1 FROM cron.job WHERE jobname = 'expire-trials-job'
);

-- 매 시간 정각에 expire_trials() 실행
SELECT cron.schedule(
  'expire-trials-job',       -- Job 이름
  '0 * * * *',               -- 매 시간 정각 (Cron 표현식)
  $$SELECT public.expire_trials()$$
);

-- =====================================================
-- 확인 방법:
-- SELECT * FROM cron.job;
-- 
-- 수동 실행 (테스트용):
-- SELECT public.expire_trials();
--
-- 로그 확인:
-- SELECT * FROM cron.job_run_details ORDER BY start_time DESC LIMIT 10;
-- =====================================================
