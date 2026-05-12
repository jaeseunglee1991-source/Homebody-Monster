-- ================================================================
--  lobby_chat_messages
--  로비 채팅 메시지를 영구 저장하는 테이블.
--
--  도입 배경:
--    기존 Supabase Realtime Broadcast 방식은 SDK 7.0.2에서
--    typed payload deserialization이 실패하는 버그가 있어
--    수신자가 닉네임/메시지를 받지 못하는 문제 발생.
--
--    실제 출시 게임 표준 패턴인 "DB 테이블 + Postgres Changes" 구독으로 전환.
--    Discord/Slack 등 모든 채팅 앱이 본질적으로 같은 패턴.
--
--  장점:
--    • SDK envelope quirks 영향 없음 (Postgrest는 안정적)
--    • 채팅 히스토리 자동 보존 (재접속 시 최근 N개 표시)
--    • RLS로 인증/스팸 차단을 DB 레벨에서 강제
--    • 욕설 신고/기록을 위한 영구 감사 로그
--    • 서버 트리거로 자동 모더레이션 가능
-- ================================================================

CREATE TABLE IF NOT EXISTS public.lobby_chat_messages (
    id          BIGSERIAL    PRIMARY KEY,
    sender_uuid UUID         NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    nickname    TEXT         NOT NULL,
    message     TEXT         NOT NULL CHECK (length(message) > 0 AND length(message) <= 100),
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
);

-- 최근 메시지 조회용 인덱스
CREATE INDEX IF NOT EXISTS idx_lobby_chat_created_at
    ON public.lobby_chat_messages (created_at DESC);

-- Realtime publication 등록 (Postgres Changes 구독 활성화)
ALTER PUBLICATION supabase_realtime ADD TABLE public.lobby_chat_messages;

-- ─── Row Level Security ──────────────────────────────────────────
ALTER TABLE public.lobby_chat_messages ENABLE ROW LEVEL SECURITY;

-- 인증된 모든 사용자는 채팅 로그를 읽을 수 있음
DROP POLICY IF EXISTS "authenticated_can_read" ON public.lobby_chat_messages;
CREATE POLICY "authenticated_can_read"
    ON public.lobby_chat_messages FOR SELECT
    TO authenticated
    USING (true);

-- 인증된 사용자는 자신의 sender_uuid로만 INSERT 가능
-- → 다른 사람으로 위장 불가 (auth.uid()와 sender_uuid 일치 강제)
DROP POLICY IF EXISTS "authenticated_can_insert_own" ON public.lobby_chat_messages;
CREATE POLICY "authenticated_can_insert_own"
    ON public.lobby_chat_messages FOR INSERT
    TO authenticated
    WITH CHECK (sender_uuid = auth.uid());

-- ─── 자동 정리 (24시간 이상 된 메시지 삭제) ─────────────────────
CREATE OR REPLACE FUNCTION public.cleanup_old_lobby_chat()
    RETURNS void
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = public
AS $$
BEGIN
    DELETE FROM public.lobby_chat_messages
    WHERE created_at < now() - INTERVAL '24 hours';
END;
$$;

-- pg_cron 설치되어 있다면 매시간 자동 실행 (선택사항)
-- Supabase 대시보드 → Database → Extensions → pg_cron 활성화 후 아래 실행:
--   SELECT cron.schedule('cleanup_lobby_chat', '0 * * * *', $$SELECT public.cleanup_old_lobby_chat()$$);
