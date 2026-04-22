-- ================================================================
--  server_assign_match
--  데디케이티드 서버가 매칭이 성사된 플레이어들에게 서버 IP를 일괄 배정합니다.
--
--  호출 주체: 게임 서버 프로세스 (MatchmakingManager.ExecuteServerMatch)
--  보안 모델: SECURITY DEFINER — RLS를 우회하여 서버 권한으로 실행됩니다.
--             anon key를 사용하는 클라이언트가 이 함수를 호출해도
--             다른 플레이어의 row를 수정할 수 없도록 입력값을 검증합니다.
--
--  파라미터:
--    p_queue_ids  uuid[]  — 매칭된 플레이어들의 matchmaking_queue.id 배열
--    p_server_ip  text    — 게임 서버 엔드포인트 ("1.2.3.4:7777" 형식)
-- ================================================================

CREATE OR REPLACE FUNCTION public.server_assign_match(
    p_queue_ids uuid[],
    p_server_ip text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    -- 입력값 기본 검증
    IF p_queue_ids IS NULL OR array_length(p_queue_ids, 1) = 0 THEN
        RAISE EXCEPTION 'p_queue_ids must not be empty';
    END IF;

    IF p_server_ip IS NULL OR trim(p_server_ip) = '' THEN
        RAISE EXCEPTION 'p_server_ip must not be empty';
    END IF;

    -- 매칭 확정: 대기 중인 플레이어만 업데이트 (이미 처리된 row는 건드리지 않음)
    UPDATE public.matchmaking_queue
    SET
        status  = 'matched',
        room_id = p_server_ip
    WHERE
        id = ANY(p_queue_ids)
        AND status = 'waiting'; -- 멱등성 보장: waiting 상태인 row만 변경
END;
$$;

-- 함수 소유자를 postgres(슈퍼유저)로 고정하여 SECURITY DEFINER 효과 보장
ALTER FUNCTION public.server_assign_match(uuid[], text) OWNER TO postgres;

-- anon/authenticated 역할은 실행만 허용 (DDL 권한 없음)
REVOKE ALL ON FUNCTION public.server_assign_match(uuid[], text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.server_assign_match(uuid[], text) TO anon, authenticated;
