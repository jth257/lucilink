-- =====================================================
-- 마이그레이션: payment_logs 테이블 + 구독 해지 함수
-- Supabase SQL Editor에서 실행
-- =====================================================

-- ===== 1. 결제 내역 테이블 =====
CREATE TABLE IF NOT EXISTS public.payment_logs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  product_id UUID NOT NULL REFERENCES public.products(id),
  order_id TEXT NOT NULL UNIQUE,           -- ORDER_{timestamp}_{userId}
  payment_key TEXT,                         -- 토스페이먼츠 paymentKey
  amount INTEGER NOT NULL,                 -- 결제 금액 (원)
  currency TEXT NOT NULL DEFAULT 'KRW',
  status TEXT NOT NULL DEFAULT 'pending',   -- pending, done, cancelled, refunded
  provider TEXT NOT NULL DEFAULT 'toss',    -- toss, stripe 등
  metadata JSONB,                           -- 추가 결제 정보 (카드사, 영수증URL 등)
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- RLS: 본인 결제 내역만 조회
ALTER TABLE public.payment_logs ENABLE ROW LEVEL SECURITY;
CREATE POLICY "Users can view own payments"
  ON public.payment_logs FOR SELECT
  USING (auth.uid() = user_id);

-- 서비스 역할만 삽입/수정 가능
CREATE POLICY "Service can insert payments"
  ON public.payment_logs FOR INSERT
  WITH CHECK (true);

CREATE POLICY "Service can update payments"
  ON public.payment_logs FOR UPDATE
  USING (true);

-- ===== 2. 구독 해지 함수 =====
CREATE OR REPLACE FUNCTION public.cancel_subscription(p_product_slug TEXT)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_product_id UUID;
  v_sub RECORD;
BEGIN
  SELECT id INTO v_product_id FROM public.products WHERE slug = p_product_slug LIMIT 1;
  IF v_product_id IS NULL THEN
    RETURN jsonb_build_object('success', false, 'error', 'product_not_found');
  END IF;

  SELECT * INTO v_sub FROM public.user_subscriptions
  WHERE user_id = auth.uid() AND product_id = v_product_id;

  IF NOT FOUND THEN
    RETURN jsonb_build_object('success', false, 'error', 'no_subscription');
  END IF;

  IF v_sub.status != 'active' THEN
    RETURN jsonb_build_object('success', false, 'error', 'not_active',
      'message', '활성 구독만 해지할 수 있습니다.');
  END IF;

  -- 즉시 해지가 아닌 기간 만료 해지 (current_period_end까지 사용 가능)
  UPDATE public.user_subscriptions
  SET status = 'cancelled', updated_at = now()
  WHERE id = v_sub.id;

  RETURN jsonb_build_object(
    'success', true,
    'status', 'cancelled',
    'valid_until', v_sub.current_period_end,
    'message', '구독이 해지되었습니다. 결제 기간 종료일까지 이용 가능합니다.'
  );
END;
$$;

-- ===== 3. updated_at 자동 갱신 트리거 (payment_logs) =====
CREATE TRIGGER update_payment_logs_modtime
  BEFORE UPDATE ON public.payment_logs
  FOR EACH ROW
  EXECUTE FUNCTION public.update_modified_column();
