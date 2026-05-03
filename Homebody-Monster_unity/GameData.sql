-- ================================================================
--  Homebody-Monster  — Supabase 스키마 정의
--  실행 방법: Supabase Dashboard > SQL Editor에 붙여넣고 실행
-- ================================================================

-- ── 1. profiles (기존 테이블, 이미 있으면 skip) ─────────────────
CREATE TABLE IF NOT EXISTS public.profiles (
    id                  UUID PRIMARY KEY REFERENCES auth.users(id) ON DELETE CASCADE,
    nickname            TEXT NOT NULL DEFAULT '',
    win_count           INT  NOT NULL DEFAULT 0,
    lose_count          INT  NOT NULL DEFAULT 0,
    pizza_count         INT  NOT NULL DEFAULT 0,
    revive_ticket_count INT  NOT NULL DEFAULT 0,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE public.profiles ENABLE ROW LEVEL SECURITY;

CREATE POLICY IF NOT EXISTS "profiles_select_own"
    ON public.profiles FOR SELECT USING (auth.uid() = id);
CREATE POLICY IF NOT EXISTS "profiles_update_own"
    ON public.profiles FOR UPDATE USING (auth.uid() = id);

-- ── 2. match_history ────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS public.match_history (
    id               BIGSERIAL    PRIMARY KEY,
    user_id          UUID         NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    is_win           BOOLEAN      NOT NULL DEFAULT FALSE,
    rank             INT          NOT NULL DEFAULT 0,
    kills            INT          NOT NULL DEFAULT 0,
    survival_seconds INT          NOT NULL DEFAULT 0,
    played_at        TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_match_history_user_id
    ON public.match_history (user_id, played_at DESC);

ALTER TABLE public.match_history ENABLE ROW LEVEL SECURITY;

-- 본인 전적만 조회/삽입 가능
CREATE POLICY IF NOT EXISTS "match_history_select_own"
    ON public.match_history FOR SELECT USING (auth.uid() = user_id);
CREATE POLICY IF NOT EXISTS "match_history_insert_own"
    ON public.match_history FOR INSERT WITH CHECK (auth.uid() = user_id);

-- ── 3. leaderboard_kills (View) ─────────────────────────────────
CREATE OR REPLACE VIEW public.leaderboard_kills AS
SELECT
    mh.user_id,
    p.nickname,
    SUM(mh.kills)        AS total_kills,
    COUNT(*) FILTER (WHERE mh.is_win) AS wins
FROM   public.match_history mh
JOIN   public.profiles      p ON p.id = mh.user_id
GROUP  BY mh.user_id, p.nickname
ORDER  BY total_kills DESC;

-- leaderboard_kills 뷰: 전체 공개
GRANT SELECT ON public.leaderboard_kills TO anon, authenticated;

-- ── 4. ban_logs ─────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS public.ban_logs (
    id         BIGSERIAL   PRIMARY KEY,
    user_id    UUID        REFERENCES auth.users(id) ON DELETE SET NULL,
    nickname   TEXT        NOT NULL DEFAULT '',
    reason     TEXT        NOT NULL DEFAULT '',
    banned_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE public.ban_logs ENABLE ROW LEVEL SECURITY;

-- ban_logs: Service Role Key만 INSERT 가능 (클라이언트 직접 접근 불가)
-- 서버(dedicated server)는 SUPABASE_SERVICE_ROLE_KEY를 사용합니다.
CREATE POLICY IF NOT EXISTS "ban_logs_insert_service_only"
    ON public.ban_logs FOR INSERT
    WITH CHECK (auth.role() = 'service_role');

-- ── 5. reconnect_grace ──────────────────────────────────────────
-- 재접속 유예 시간 추적 테이블 (서버가 플레이어 슬롯을 보존하는 데 사용)
CREATE TABLE IF NOT EXISTS public.reconnect_grace (
    user_id      UUID        PRIMARY KEY REFERENCES auth.users(id) ON DELETE CASCADE,
    room_id      TEXT        NOT NULL,
    disconnected_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    grace_until  TIMESTAMPTZ NOT NULL DEFAULT now() + INTERVAL '30 seconds'
);

ALTER TABLE public.reconnect_grace ENABLE ROW LEVEL SECURITY;

CREATE POLICY IF NOT EXISTS "reconnect_grace_own"
    ON public.reconnect_grace FOR ALL USING (auth.uid() = user_id);

-- ── 6. 기존 save_match_result RPC (참고용, 이미 있으면 skip) ──────
-- 이 함수가 없는 경우에만 아래 블록을 실행하세요.
/*
CREATE OR REPLACE FUNCTION public.save_match_result(
    p_room_id      TEXT,
    p_is_winner    BOOLEAN,
    p_rank         INT,
    p_kill_count   INT,
    p_survived_time FLOAT
) RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
    INSERT INTO public.match_history (user_id, is_win, rank, kills, survival_seconds)
    VALUES (auth.uid(), p_is_winner, p_rank, p_kill_count, p_survived_time::INT);
END;
$$;
*/
