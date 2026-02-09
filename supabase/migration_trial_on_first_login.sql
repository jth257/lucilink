-- =====================================================
-- 체험 시작 시점 변경: 회원가입 → WPF 앱 첫 로그인
-- Supabase SQL Editor에서 실행
-- =====================================================

-- 1. 기존 트리거 함수 교체 (pending 상태로 생성, trial_end_date = NULL)
CREATE OR REPLACE FUNCTION public.create_default_trial()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_product_id UUID;
BEGIN
  SELECT id INTO v_product_id
  FROM public.products
  WHERE slug = 'lucilink' AND is_active = true
  LIMIT 1;

  IF v_product_id IS NOT NULL THEN
    INSERT INTO public.user_subscriptions (user_id, product_id, status, trial_end_date)
    VALUES (NEW.id, v_product_id, 'pending', NULL)
    ON CONFLICT (user_id, product_id) DO NOTHING;
  END IF;

  RETURN NEW;
END;
$$;

-- 2. WPF 앱 첫 로그인 시 호출할 RPC 함수
CREATE OR REPLACE FUNCTION public.activate_trial(p_product_slug TEXT)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_sub RECORD;
  v_product_id UUID;
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

  IF v_sub.status != 'pending' THEN
    RETURN jsonb_build_object(
      'success', true,
      'status', v_sub.status,
      'trial_end_date', v_sub.trial_end_date,
      'already_activated', true
    );
  END IF;

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

-- 3. 기존에 이미 가입한 사용자가 있다면 trial → pending으로 변경 (아직 앱 사용 안 한 경우)
-- 필요 시 주석 해제하고 실행
-- UPDATE public.user_subscriptions SET status = 'pending', trial_end_date = NULL WHERE status = 'trial';
