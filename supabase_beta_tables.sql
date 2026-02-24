-- ============================================
-- 베타 테스터 평생 무료 시스템 - Supabase SQL
-- Supabase Dashboard > SQL Editor 에서 실행
-- ============================================

-- 1. 프로그램 첫 로그인 기록 테이블
CREATE TABLE IF NOT EXISTS beta_program_logins (
  user_id UUID REFERENCES auth.users(id) PRIMARY KEY,
  first_login_at TIMESTAMPTZ DEFAULT now(),
  device_info TEXT
);

ALTER TABLE beta_program_logins ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Users can insert own login"
  ON beta_program_logins FOR INSERT WITH CHECK (auth.uid() = user_id);
CREATE POLICY "Users can view own login"
  ON beta_program_logins FOR SELECT USING (auth.uid() = user_id);

-- 2. 베타 피드백 테이블
CREATE TABLE IF NOT EXISTS beta_feedbacks (
  id UUID DEFAULT gen_random_uuid() PRIMARY KEY,
  user_id UUID REFERENCES auth.users(id) NOT NULL,
  content TEXT NOT NULL CHECK (char_length(content) >= 10),
  category TEXT DEFAULT 'general',
  status TEXT DEFAULT 'pending',
  reviewed_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ DEFAULT now()
);

ALTER TABLE beta_feedbacks ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Users can insert own feedback"
  ON beta_feedbacks FOR INSERT WITH CHECK (auth.uid() = user_id);
CREATE POLICY "Users can view own feedback"
  ON beta_feedbacks FOR SELECT USING (auth.uid() = user_id);

-- 3. 베타 테스터 상태 확인 RPC
CREATE OR REPLACE FUNCTION check_beta_tester_status()
RETURNS JSON AS $$
DECLARE
  has_program_login BOOLEAN;
  has_approved_feedback BOOLEAN;
  feedback_status TEXT;
BEGIN
  -- 프로그램 로그인 여부
  SELECT EXISTS(
    SELECT 1 FROM beta_program_logins WHERE user_id = auth.uid()
  ) INTO has_program_login;

  -- 승인된 피드백 존재 여부
  SELECT EXISTS(
    SELECT 1 FROM beta_feedbacks
    WHERE user_id = auth.uid() AND status = 'approved'
  ) INTO has_approved_feedback;

  -- 가장 최근 피드백 상태
  SELECT bf.status INTO feedback_status
  FROM beta_feedbacks bf
  WHERE bf.user_id = auth.uid()
  ORDER BY bf.created_at DESC
  LIMIT 1;

  RETURN json_build_object(
    'has_program_login', has_program_login,
    'has_approved_feedback', has_approved_feedback,
    'is_lifetime_eligible', (has_program_login AND has_approved_feedback),
    'latest_feedback_status', COALESCE(feedback_status, 'none')
  );
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- 4. 프로그램 로그인 기록 RPC (UPSERT: 이미 있으면 무시)
CREATE OR REPLACE FUNCTION record_program_login(p_device_info TEXT DEFAULT NULL)
RETURNS JSON AS $$
BEGIN
  INSERT INTO beta_program_logins (user_id, device_info)
  VALUES (auth.uid(), p_device_info)
  ON CONFLICT (user_id) DO NOTHING;

  RETURN json_build_object('success', true);
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;
