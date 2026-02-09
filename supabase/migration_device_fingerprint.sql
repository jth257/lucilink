-- =====================================================
-- 마이그레이션: activate_trial에 기기 핑거프린트 검사 추가
-- Supabase SQL Editor에서 실행
-- =====================================================

-- 기존 activate_trial 함수를 기기 검사 포함 버전으로 교체
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

  -- 이미 활성화된 경우 현재 상태 반환 + 기기 등록
  IF v_sub.status != 'pending' THEN
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
        AND df.user_id != auth.uid()
    ) INTO v_device_used;

    IF v_device_used THEN
      RETURN jsonb_build_object(
        'success', false,
        'error', 'device_already_used',
        'message', '이 기기에서 이미 무료 체험을 사용한 기록이 있습니다.'
      );
    END IF;

    INSERT INTO public.device_fingerprints (device_hash, user_id, product_id)
    VALUES (p_device_hash, auth.uid(), v_product_id)
    ON CONFLICT (device_hash, product_id) DO NOTHING;
  END IF;

  -- pending → trial 전환
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
