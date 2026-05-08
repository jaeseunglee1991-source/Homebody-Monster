-- ================================================================
--  Homebody-Monster  — Supabase 스키마 정의
--  실행 방법: Supabase Dashboard > SQL Editor에 붙여넣고 실행
--
--  ⚠️ 운영 DB (project: khvimlswbbmxcxpjkkes) 가 SOURCE OF TRUTH 입니다.
--  본 파일의 일부 컬럼명/타입은 운영 DB 와 다를 수 있으며,
--  실제 적용된 마이그레이션은 Supabase Dashboard 의 migrations 탭을 참조하세요.
--  운영 DB 와 다른 부분 (예시):
--    - match_history.player_id (본 파일은 user_id 로 표기됨)
--    - match_history.created_at (본 파일은 played_at 로 표기됨)
--    - matchmaking_queue 에 room_id 컬럼 존재, updated_at 없음
-- ================================================================

-- ────────────────────────────────────────────────────────────────
--  [멱등성 마이그레이션 — 운영 DB 적용 완료, 2026-05-08]
--  매치 보상 중복 지급 차단을 위해 (player_id, room_id, ad_doubled) 추적.
-- ────────────────────────────────────────────────────────────────
-- ALTER TABLE public.match_history
--     ADD CONSTRAINT match_history_player_room_unique UNIQUE (player_id, room_id);
--
-- CREATE TABLE IF NOT EXISTS public.match_reward_grants (
--     player_id   UUID    NOT NULL REFERENCES public.profiles(id) ON DELETE CASCADE,
--     room_id     TEXT    NOT NULL,
--     ad_doubled  BOOLEAN NOT NULL DEFAULT false,
--     pizza_paid  INT     NOT NULL,
--     granted_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
--     PRIMARY KEY (player_id, room_id, ad_doubled)
-- );
-- ALTER TABLE public.match_reward_grants ENABLE ROW LEVEL SECURITY;
-- CREATE POLICY "reward_grants_select_own"
--     ON public.match_reward_grants FOR SELECT USING (auth.uid() = player_id);
--
-- grant_match_rewards 시그니처:
--   (p_rank int, p_kill_count int, p_ad_doubled bool DEFAULT false, p_room_id text DEFAULT NULL)
--   p_room_id 전달 시 PK 충돌로 INSERT 0행이면 0 반환 (멱등).
--   p_room_id NULL 이면 legacy 동작 (멱등성 없음, 호환성 유지).
-- ────────────────────────────────────────────────────────────────

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

-- ── 7. matchmaking_queue ─────────────────────────────────────────
-- 클라이언트가 매칭 대기열에 자신을 등록하는 테이블.
-- S-1: player_id = auth.uid() 강제로 타인 ID 사칭 INSERT 차단.
CREATE TABLE IF NOT EXISTS public.matchmaking_queue (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id   UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    nickname    TEXT        NOT NULL DEFAULT '',
    room_id     TEXT,
    status      TEXT        NOT NULL DEFAULT 'waiting',  -- waiting | matched | cancelled
    joined_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Realtime 필터 (player_id=eq.xxx / id=eq.xxx) 가 UPDATE/DELETE 에서도 동작하려면 FULL 필요
ALTER TABLE public.matchmaking_queue REPLICA IDENTITY FULL;
ALTER TABLE public.private_rooms     REPLICA IDENTITY FULL;  -- H-15 필터 동작

ALTER TABLE public.matchmaking_queue ENABLE ROW LEVEL SECURITY;

-- 본인 행만 INSERT 가능 (S-1 핵심 정책) + nickname 길이 검증
CREATE POLICY IF NOT EXISTS "queue_insert_own"
    ON public.matchmaking_queue FOR INSERT
    WITH CHECK ((auth.uid() = player_id) AND (length(nickname) >= 2));

-- 매칭 알고리즘이 다른 대기자의 'waiting' 행을 볼 수 있어야 하므로 SELECT는 status=waiting 또는 본인 행
CREATE POLICY IF NOT EXISTS "queue_select_waiting"
    ON public.matchmaking_queue FOR SELECT
    USING ((status = 'waiting') OR (auth.uid() = player_id));

-- 본인 행만 UPDATE 가능 (status 변경 등)
CREATE POLICY IF NOT EXISTS "queue_update_own"
    ON public.matchmaking_queue FOR UPDATE
    USING (auth.uid() = player_id);

-- 본인 행만 DELETE 가능 (큐 이탈)
CREATE POLICY IF NOT EXISTS "queue_delete_own"
    ON public.matchmaking_queue FOR DELETE
    USING (auth.uid() = player_id);

-- service_role 은 RLS 를 우회하므로 별도 정책 불필요.

-- ── 8. leave_matchmaking_queue RPC ──────────────────────────────
-- 클라이언트가 매칭 대기를 취소할 때 호출 (본인 행만 삭제)
CREATE OR REPLACE FUNCTION public.leave_matchmaking_queue(
    p_player_id UUID
) RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
    -- SECURITY DEFINER이지만 auth.uid() 일치 검증으로 타인 행 삭제 차단
    IF auth.uid() != p_player_id THEN
        RAISE EXCEPTION 'Unauthorized';
    END IF;
    DELETE FROM public.matchmaking_queue WHERE player_id = p_player_id;
END;
$$;

-- ── 9. update_queue_status RPC ───────────────────────────────────
-- H-23: server_assign_match 실패 시 서버가 큐 상태를 cancelled 로 갱신
CREATE OR REPLACE FUNCTION public.update_queue_status(
    p_queue_ids UUID[],
    p_status    TEXT
) RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
    -- 클라이언트(anon/authenticated) 호출 차단: service_role 만 허용
    IF auth.role() != 'service_role' THEN
        RAISE EXCEPTION 'Unauthorized: service_role required';
    END IF;
    IF p_status NOT IN ('waiting','matched','cancelled') THEN
        RAISE EXCEPTION 'Invalid status value: %', p_status;
    END IF;
    UPDATE public.matchmaking_queue
    SET    status = p_status
    WHERE  id = ANY(p_queue_ids);
END;
$$;

REVOKE ALL ON FUNCTION public.update_queue_status(UUID[], TEXT) FROM PUBLIC, anon, authenticated;
GRANT  EXECUTE ON FUNCTION public.update_queue_status(UUID[], TEXT) TO service_role;
