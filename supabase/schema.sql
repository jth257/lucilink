-- =====================================================
-- Lucitella 통합 DB 스키마
-- 멀티 프로덕트 지원 (LuciLink, 향후 Luci 등)
-- Supabase SQL Editor에 붙여넣기하여 실행
-- =====================================================

-- ===== 1. 제품 마스터 테이블 =====
CREATE TABLE public.products (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  slug TEXT NOT NULL UNIQUE,           -- 'lucilink', 'luci' 등
  name TEXT NOT NULL,                  -- 'LuciLink'
  description TEXT,
  price_krw INTEGER NOT NULL DEFAULT 0,
  trial_days INTEGER NOT NULL DEFAULT 3,
  is_active BOOLEAN NOT NULL DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 초기 제품 데이터
INSERT INTO public.products (slug, name, description, price_krw, trial_days)
VALUES ('lucilink', 'LuciLink', 'Android-PC Bridge for AI Development', 9900, 3);

-- ===== 2. 사용자 구독 테이블 =====
CREATE TABLE public.user_subscriptions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  product_id UUID NOT NULL REFERENCES public.products(id),
  status TEXT NOT NULL DEFAULT 'trial',  -- 'trial', 'active', 'expired', 'cancelled'
  trial_end_date TIMESTAMPTZ,
  current_period_end TIMESTAMPTZ,
  payment_provider TEXT,                 -- 'toss', 'stripe' 등
  payment_customer_id TEXT,              -- PG사 고객 ID
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),

  UNIQUE(user_id, product_id)            -- 사용자당 제품별 구독 1개
);

-- ===== 3. 기기 핑거프린트 테이블 (어뷰징 방지) =====
CREATE TABLE public.device_fingerprints (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  device_hash TEXT NOT NULL,              -- SHA-256 해시값만 저장 (원본 불가역)
  user_id UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  product_id UUID NOT NULL REFERENCES public.products(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 같은 기기에서 같은 제품 중복 등록 방지
CREATE UNIQUE INDEX idx_device_product ON public.device_fingerprints(device_hash, product_id);

-- (app_versions 테이블 제거됨 — 업데이트는 GitHub Releases로 관리)

-- ===== 5. RLS (Row Level Security) 정책 =====

-- products: 누구나 조회 가능 (공개 정보)
ALTER TABLE public.products ENABLE ROW LEVEL SECURITY;
CREATE POLICY "Anyone can view active products"
  ON public.products FOR SELECT
  USING (is_active = true);

-- user_subscriptions: 본인 구독만 조회/수정
ALTER TABLE public.user_subscriptions ENABLE ROW LEVEL SECURITY;
CREATE POLICY "Users can view own subscriptions"
  ON public.user_subscriptions FOR SELECT
  USING (auth.uid() = user_id);

CREATE POLICY "Users can update own subscriptions"
  ON public.user_subscriptions FOR UPDATE
  USING (auth.uid() = user_id);

-- device_fingerprints: 본인 기기만 조회, 삽입은 서비스 역할로만
ALTER TABLE public.device_fingerprints ENABLE ROW LEVEL SECURITY;
CREATE POLICY "Users can view own devices"
  ON public.device_fingerprints FOR SELECT
  USING (auth.uid() = user_id);

CREATE POLICY "Users can register own devices"
  ON public.device_fingerprints FOR INSERT
  WITH CHECK (auth.uid() = user_id);


-- ===== 6. 회원가입 시 구독 레코드 생성 (체험은 WPF 앱 첫 로그인 시 활성화) =====
CREATE OR REPLACE FUNCTION public.create_default_trial()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_product_id UUID;
BEGIN
  -- LuciLink 제품 ID 조회
  SELECT id INTO v_product_id
  FROM public.products
  WHERE slug = 'lucilink' AND is_active = true
  LIMIT 1;

  IF v_product_id IS NOT NULL THEN
    -- status='pending', trial_end_date=NULL
    -- 실제 3일 체험은 WPF 앱에서 첫 로그인 시 activate_trial() 호출로 시작
    INSERT INTO public.user_subscriptions (user_id, product_id, status, trial_end_date)
    VALUES (NEW.id, v_product_id, 'pending', NULL)
    ON CONFLICT (user_id, product_id) DO NOTHING;
  END IF;

  RETURN NEW;
END;
$$;

CREATE TRIGGER on_auth_user_created
  AFTER INSERT ON auth.users
  FOR EACH ROW
  EXECUTE FUNCTION public.create_default_trial();

-- ===== 6-1. WPF 앱 첫 로그인 시 체험 활성화 + 기기 등록 함수 =====
-- 기기 핑거프린트 중복 검사 포함 (어뷰징 방지)
CREATE OR REPLACE FUNCTION public.activate_trial(p_product_slug TEXT, p_device_hash TEXT DEFAULT NULL)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_sub RECORD;
  v_product_id UUID;
  v_device_used BOOLEAN;
BEGIN
  SELECT id INTO v_product_id FROM public.products WHERE slug = p_product_slug LIMIT 1;

  IF v_product_id IS NULL THEN
    RETURN jsonb_build_object('success', false, 'error', 'product_not_found');
  END IF;

  SELECT * INTO v_sub
  FROM public.user_subscriptions
  WHERE user_id = auth.uid() AND product_id = v_product_id;

  IF NOT FOUND THEN
    RETURN jsonb_build_object('success', false, 'error', 'no_subscription');
  END IF;

  -- 이미 활성화된 경우 현재 상태 반환
  IF v_sub.status != 'pending' THEN
    -- 기기 등록은 시도 (이미 활성 사용자가 새 기기에서 로그인)
    IF p_device_hash IS NOT NULL THEN
      INSERT INTO public.device_fingerprints (device_hash, user_id, product_id)
      VALUES (p_device_hash, auth.uid(), v_product_id)
      ON CONFLICT (device_hash, product_id) DO NOTHING;
    END IF;

    RETURN jsonb_build_object(
      'success', true,
      'status', v_sub.status,
      'trial_end_date', v_sub.trial_end_date,
      'already_activated', true
    );
  END IF;

  -- === 기기 핑거프린트 중복 체험 검사 ===
  IF p_device_hash IS NOT NULL THEN
    SELECT EXISTS(
      SELECT 1
      FROM public.device_fingerprints df
      WHERE df.device_hash = p_device_hash
        AND df.product_id = v_product_id
        AND df.user_id != auth.uid()  -- 다른 사용자가 이 기기로 이미 체험함
    ) INTO v_device_used;

    IF v_device_used THEN
      RETURN jsonb_build_object(
        'success', false,
        'error', 'device_already_used',
        'message', '이 기기에서 이미 무료 체험을 사용한 기록이 있습니다. 구독을 구매해주세요.'
      );
    END IF;

    -- 기기 등록
    INSERT INTO public.device_fingerprints (device_hash, user_id, product_id)
    VALUES (p_device_hash, auth.uid(), v_product_id)
    ON CONFLICT (device_hash, product_id) DO NOTHING;
  END IF;

  -- pending → trial 전환, 지금부터 3일
  UPDATE public.user_subscriptions
  SET status = 'trial', trial_end_date = now() + interval '3 days', updated_at = now()
  WHERE id = v_sub.id;

  RETURN jsonb_build_object(
    'success', true,
    'status', 'trial',
    'trial_end_date', now() + interval '3 days',
    'already_activated', false
  );
END;
$$;

-- ===== 7. 체험 만료 자동 감지 함수 (Cron 또는 앱에서 호출) =====
CREATE OR REPLACE FUNCTION public.expire_trials()
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
  UPDATE public.user_subscriptions
  SET status = 'expired', updated_at = now()
  WHERE status = 'trial'
    AND trial_end_date < now();
END;
$$;

-- ===== 8. 핑거프린트 중복 체험 검사 함수 =====
CREATE OR REPLACE FUNCTION public.check_device_trial(
  p_device_hash TEXT,
  p_product_slug TEXT
)
RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_exists BOOLEAN;
BEGIN
  SELECT EXISTS(
    SELECT 1
    FROM public.device_fingerprints df
    JOIN public.products p ON p.id = df.product_id
    WHERE df.device_hash = p_device_hash
      AND p.slug = p_product_slug
  ) INTO v_exists;

  RETURN v_exists;  -- true = 이미 체험한 기기
END;
$$;

-- ===== 9. updated_at 자동 갱신 트리거 =====
CREATE OR REPLACE FUNCTION public.update_modified_column()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$;

CREATE TRIGGER update_user_subscriptions_modtime
  BEFORE UPDATE ON public.user_subscriptions
  FOR EACH ROW
  EXECUTE FUNCTION public.update_modified_column();
