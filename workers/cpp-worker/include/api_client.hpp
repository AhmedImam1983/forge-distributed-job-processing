#pragma once

#include <nlohmann/json.hpp>

#include <chrono>
#include <mutex>
#include <string>

namespace forge::worker {

struct HttpResponse {
    long status = 0;
    std::string body;
    bool transport_ok = false;
    std::string error;
};

class ApiClient {
public:
    ApiClient();

    [[nodiscard]] static std::string new_guid();

    HttpResponse post_json(
        const std::string& path,
        const nlohmann::json& payload);

    HttpResponse claim_job(
        const std::string& job_id,
        const std::string& claim_request_id,
        const std::string& worker_instance_id);

    HttpResponse renew_lease(
        const std::string& job_id,
        const std::string& attempt_id,
        const std::string& worker_instance_id,
        long long fencing_token,
        const std::string& renew_request_id);

    HttpResponse complete_succeeded(
        const std::string& job_id,
        const std::string& attempt_id,
        const std::string& worker_instance_id,
        long long fencing_token,
        const std::string& completion_request_id,
        const nlohmann::json& result_descriptor);

    HttpResponse complete_failed(
        const std::string& job_id,
        const std::string& attempt_id,
        const std::string& worker_instance_id,
        long long fencing_token,
        const std::string& completion_request_id,
        const std::string& failure_code,
        const std::string& failure_message);

    HttpResponse complete_cancelled(
        const std::string& job_id,
        const std::string& attempt_id,
        const std::string& worker_instance_id,
        long long fencing_token,
        const std::string& completion_request_id,
        const std::string& cancellation_details = {});

private:
    std::string acquire_token();
    HttpResponse perform_post(
        const std::string& url,
        const std::string& body,
        const std::string& bearer_token);

    std::string api_base_url_;
    std::string keycloak_base_url_;
    std::string realm_;
    std::string client_id_;
    std::string client_secret_;

    std::mutex token_mutex_;
    std::string cached_token_;
    std::chrono::steady_clock::time_point token_expiry_{};
};

}
