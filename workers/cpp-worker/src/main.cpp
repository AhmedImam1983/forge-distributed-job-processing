#include "api_client.hpp"
#include "job_handler.hpp"

#include <rabbitmq-c/amqp.h>
#include <rabbitmq-c/tcp_socket.h>
#include <nlohmann/json.hpp>

#include <cerrno>
#include <cstdlib>
#include <iostream>
#include <stdexcept>
#include <string>
#include <sys/time.h>

using nlohmann::json;

namespace {

std::string env_or(const char* key, const char* fallback)
{
    const char* value = std::getenv(key);
    return value && *value ? value : fallback;
}

int port_from_environment()
{
    const auto value = env_or("FORGE_RABBITMQ_PORT", "5673");
    char* end = nullptr;
    errno = 0;
    const auto port = std::strtol(value.c_str(), &end, 10);

    if (errno || end == value.c_str() || *end != '\0' ||
        port < 1 || port > 65535)
        throw std::runtime_error("FORGE_RABBITMQ_PORT is invalid.");

    return static_cast<int>(port);
}

void require_reply(const amqp_rpc_reply_t& reply, const char* step)
{
    if (reply.reply_type == AMQP_RESPONSE_NORMAL)
        return;

    if (reply.reply_type == AMQP_RESPONSE_LIBRARY_EXCEPTION)
        throw std::runtime_error(
            std::string(step) + " failed: " +
            amqp_error_string2(reply.library_error));

    if (reply.reply_type == AMQP_RESPONSE_SERVER_EXCEPTION)
        throw std::runtime_error(
            std::string(step) + " was rejected by RabbitMQ.");

    throw std::runtime_error(
        std::string(step) + " failed with an unexpected broker response.");
}

class Connection {
public:
    Connection(
        const std::string& host,
        int port,
        const std::string& vhost,
        const std::string& username,
        const std::string& password)
        : connection_(amqp_new_connection())
    {
        if (!connection_)
            throw std::runtime_error("Could not allocate RabbitMQ connection.");

        socket_ = amqp_tcp_socket_new(connection_);
        if (!socket_) {
            close_noexcept();
            throw std::runtime_error("Could not allocate RabbitMQ TCP socket.");
        }

        const int socket_result =
            amqp_socket_open(socket_, host.c_str(), port);

        if (socket_result != AMQP_STATUS_OK) {
            const std::string error = amqp_error_string2(socket_result);
            close_noexcept();
            throw std::runtime_error("RabbitMQ connection failed: " + error);
        }

        try {
            require_reply(
                amqp_login(
                    connection_, vhost.c_str(), 0, 131072, 0,
                    AMQP_SASL_METHOD_PLAIN,
                    username.c_str(), password.c_str()),
                "RabbitMQ login");

            amqp_channel_open(connection_, 1);
            require_reply(amqp_get_rpc_reply(connection_), "Opening channel");
            channel_open_ = true;
        } catch (...) {
            close_noexcept();
            throw;
        }
    }

    ~Connection() { close_noexcept(); }

    Connection(const Connection&) = delete;
    Connection& operator=(const Connection&) = delete;

    amqp_connection_state_t get() const { return connection_; }

    void ensure_topology(
        const std::string& exchange,
        const std::string& queue,
        const std::string& routing_key,
        const std::string& dead_letter_exchange,
        const std::string& dead_letter_queue,
        const std::string& dead_letter_routing_key)
    {
        amqp_exchange_declare(
            connection_, 1,
            amqp_cstring_bytes(exchange.c_str()),
            amqp_cstring_bytes("direct"),
            0, 1, 0, 0, amqp_empty_table);
        require_reply(amqp_get_rpc_reply(connection_), "Declaring exchange");

        const bool use_dead_letter = !dead_letter_exchange.empty();

        if (use_dead_letter)
        {
            amqp_exchange_declare(
                connection_, 1,
                amqp_cstring_bytes(dead_letter_exchange.c_str()),
                amqp_cstring_bytes("direct"),
                0, 1, 0, 0, amqp_empty_table);
            require_reply(
                amqp_get_rpc_reply(connection_),
                "Declaring dead-letter exchange");

            amqp_queue_declare(
                connection_, 1,
                amqp_cstring_bytes(dead_letter_queue.c_str()),
                0, 1, 0, 0, amqp_empty_table);
            require_reply(
                amqp_get_rpc_reply(connection_),
                "Declaring dead-letter queue");

            amqp_queue_bind(
                connection_, 1,
                amqp_cstring_bytes(dead_letter_queue.c_str()),
                amqp_cstring_bytes(dead_letter_exchange.c_str()),
                amqp_cstring_bytes(dead_letter_routing_key.c_str()),
                amqp_empty_table);
            require_reply(
                amqp_get_rpc_reply(connection_),
                "Binding dead-letter queue");
        }

        amqp_table_t queue_arguments = amqp_empty_table;
        amqp_table_entry_t dead_letter_arguments[2]{};

        if (use_dead_letter)
        {
            dead_letter_arguments[0].key =
                amqp_cstring_bytes("x-dead-letter-exchange");
            dead_letter_arguments[0].value.kind = AMQP_FIELD_KIND_UTF8;
            dead_letter_arguments[0].value.value.bytes =
                amqp_cstring_bytes(dead_letter_exchange.c_str());

            dead_letter_arguments[1].key =
                amqp_cstring_bytes("x-dead-letter-routing-key");
            dead_letter_arguments[1].value.kind = AMQP_FIELD_KIND_UTF8;
            dead_letter_arguments[1].value.value.bytes =
                amqp_cstring_bytes(dead_letter_routing_key.c_str());

            queue_arguments.num_entries = 2;
            queue_arguments.entries = dead_letter_arguments;
        }

        amqp_queue_declare(
            connection_, 1,
            amqp_cstring_bytes(queue.c_str()),
            0, 1, 0, 0, queue_arguments);
        require_reply(amqp_get_rpc_reply(connection_), "Declaring queue");

        amqp_queue_bind(
            connection_, 1,
            amqp_cstring_bytes(queue.c_str()),
            amqp_cstring_bytes(exchange.c_str()),
            amqp_cstring_bytes(routing_key.c_str()),
            amqp_empty_table);
        require_reply(amqp_get_rpc_reply(connection_), "Binding queue");
    }

private:
    void close_noexcept() noexcept
    {
        if (!connection_)
            return;

        if (channel_open_) {
            amqp_channel_close(connection_, 1, AMQP_REPLY_SUCCESS);
            channel_open_ = false;
        }

        amqp_connection_close(connection_, AMQP_REPLY_SUCCESS);
        amqp_destroy_connection(connection_);
        connection_ = nullptr;
        socket_ = nullptr;
    }

    amqp_connection_state_t connection_ = nullptr;
    amqp_socket_t* socket_ = nullptr;
    bool channel_open_ = false;
};

std::string json_outcome(const json& value)
{
    if (value.is_object() &&
        value.contains("outcome") &&
        value["outcome"].is_string())
        return value["outcome"].get<std::string>();

    return {};
}

std::string response_outcome(const forge::worker::HttpResponse& response)
{
    try {
        if (response.body.empty())
            return {};
        return json_outcome(json::parse(response.body));
    } catch (...) {
        return {};
    }
}

void require_http_response(
    const forge::worker::HttpResponse& response,
    const char* operation)
{
    if (!response.transport_ok)
        throw std::runtime_error(
            std::string(operation) + " transport failed: " + response.error);

    if (response.status >= 500)
        throw std::runtime_error(
            std::string(operation) + " returned HTTP " +
            std::to_string(response.status));
}

bool claim_outcome_is_stale(const std::string& outcome)
{
    return outcome == "AlreadyClaimed" ||
           outcome == "NotEligible" ||
           outcome == "ExpiredOrLost";
}

bool completion_outcome_is_final(const std::string& outcome)
{
    return outcome == "AttemptAlreadyFinalized" ||
           outcome == "NotOwnerOrSuperseded" ||
           outcome == "LeaseExpiredOrLost" ||
           outcome == "CancellationPending";
}

class PoisonMessageError final : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

bool complete_delivery(
    forge::worker::ApiClient& api,
    const std::string& worker_id,
    const std::string& body)
{
    json event;
    try {
        event = json::parse(body);
    } catch (const json::exception&) {
        throw PoisonMessageError("Message body is not valid JSON.");
    }

    if (!event.is_object() ||
        !event.contains("jobId") || !event["jobId"].is_string() ||
        !event.contains("jobType") || !event["jobType"].is_string() ||
        !event.contains("messageId") || !event["messageId"].is_string())
        throw PoisonMessageError(
            "Malformed job-available message.");

    const auto job_id = event["jobId"].get<std::string>();
    const auto event_job_type = event["jobType"].get<std::string>();

    if (job_id.empty() || event["messageId"].get<std::string>().empty())
        throw PoisonMessageError(
            "Message contains an empty ID.");

    std::cout << "Received job " << job_id
              << " (" << event_job_type << ").\n";

    auto claim = api.claim_job(
        job_id, forge::worker::ApiClient::new_guid(), worker_id);

    require_http_response(claim, "Claim");

    if (claim.status == 404) {
        std::cout << "Stale notification: job not found; ACK.\n";
        return true;
    }

    if (claim.status != 200) {
        const auto outcome = response_outcome(claim);

        if (claim.status == 409 && claim_outcome_is_stale(outcome)) {
            std::cout << "Claim outcome " << outcome << "; ACK notification.\n";
            return true;
        }

        throw std::runtime_error(
            "Claim was not granted (HTTP " +
            std::to_string(claim.status) +
            ", outcome " + (outcome.empty() ? "unknown" : outcome) +
            "); refusing to ACK.");
    }

    const auto claim_json = json::parse(claim.body);
    const auto claim_outcome = json_outcome(claim_json);

    if (claim_outcome != "Granted") {
        if (claim_outcome_is_stale(claim_outcome)) {
            std::cout << "Claim outcome " << claim_outcome << "; ACK.\n";
            return true;
        }

        throw std::runtime_error(
            "Unexpected claim outcome; refusing to ACK.");
    }

    if (!claim_json.contains("attemptId") ||
        !claim_json["attemptId"].is_string() ||
        !claim_json.contains("fencingToken") ||
        !claim_json["fencingToken"].is_number_integer() ||
        !claim_json.contains("executionDescriptor") ||
        !claim_json["executionDescriptor"].is_object())
        throw std::runtime_error(
            "Granted claim has an incomplete execution descriptor.");

    const auto attempt_id = claim_json["attemptId"].get<std::string>();
    const auto fencing_token =
        claim_json["fencingToken"].get<long long>();
    const auto& descriptor = claim_json["executionDescriptor"];

    if (attempt_id.empty())
        throw std::runtime_error("Granted claim has an empty attempt ID.");

    if (!descriptor.contains("jobType") ||
        !descriptor["jobType"].is_string())
        throw std::runtime_error(
            "Execution descriptor has no jobType.");

    const auto actual_job_type = descriptor["jobType"].get<std::string>();

    forge::worker::JobHandlerResult result;

    if (actual_job_type != event_job_type) {
        result = {
            false,
            json::object(),
            "JOB_TYPE_MISMATCH",
            "Message jobType does not match the claimed job."
        };
    } else {
        result = forge::worker::handle_job(descriptor);
    }

    forge::worker::HttpResponse completion;

    if (result.succeeded) {
        completion = api.complete_succeeded(
            job_id, attempt_id, worker_id, fencing_token,
            forge::worker::ApiClient::new_guid(),
            result.result_descriptor);
    } else {
        std::cerr << "Job handler returned " << result.failure_code << ".\n";
        completion = api.complete_failed(
            job_id, attempt_id, worker_id, fencing_token,
            forge::worker::ApiClient::new_guid(),
            result.failure_code, result.failure_message);
    }

    require_http_response(completion, "Completion");

    const auto completion_outcome = response_outcome(completion);

    if (completion.status == 200 &&
        completion_outcome.rfind("Accepted", 0) == 0) {
        std::cout << "Completion accepted: " << completion_outcome << ".\n";
        return true;
    }

    if (completion.status == 404) {
        std::cout << "Job disappeared before completion; ACK stale notification.\n";
        return true;
    }

    if (completion.status == 409 &&
        completion_outcome_is_final(completion_outcome)) {
        std::cout << "Completion rejected as stale: "
                  << completion_outcome << "; ACK.\n";
        return true;
    }

    throw std::runtime_error(
        "Completion outcome is not safely acknowledged (HTTP " +
        std::to_string(completion.status) +
        ", outcome " +
        (completion_outcome.empty() ? "unknown" : completion_outcome) +
        ").");
}

void run_consumer(
    Connection& connection,
    forge::worker::ApiClient& api,
    const std::string& queue,
    bool dead_letter_enabled)
{
    auto* state = connection.get();

    amqp_basic_qos(state, 1, 0, 1, 0);
    require_reply(
        amqp_get_rpc_reply(state),
        "Configuring prefetch");

    amqp_basic_consume(
        state, 1, amqp_cstring_bytes(queue.c_str()),
        amqp_empty_bytes, 0, 0, 0, amqp_empty_table);
    require_reply(amqp_get_rpc_reply(state), "Starting consumer");

    const auto worker_id =
        std::string("forge-cpp-worker-") +
        forge::worker::ApiClient::new_guid();

    std::cout << "Worker started. Instance ID: " << worker_id << '\n'
              << "Queue: " << queue
              << " (manual ACK, prefetch=1).\n"
              << "Press Ctrl+C to stop.\n";

    for (;;) {
        amqp_maybe_release_buffers(state);

        amqp_envelope_t envelope{};
        timeval timeout{1, 0};
        const auto reply =
            amqp_consume_message(state, &envelope, &timeout, 0);

        if (reply.reply_type == AMQP_RESPONSE_LIBRARY_EXCEPTION &&
            reply.library_error == AMQP_STATUS_TIMEOUT)
            continue;

        require_reply(reply, "Receiving RabbitMQ delivery");

        const auto body = std::string(
            static_cast<const char*>(envelope.message.body.bytes),
            envelope.message.body.len);
        const auto delivery_tag = envelope.delivery_tag;

        bool should_ack = false;

        try {
            should_ack = complete_delivery(api, worker_id, body);
        } catch (const PoisonMessageError& exception) {
            if (!dead_letter_enabled) {
                amqp_destroy_envelope(&envelope);
                throw std::runtime_error(
                    std::string("Poison delivery retained for retry because "
                                "DLQ is not configured: ") + exception.what());
            }

            const int nack_status =
                amqp_basic_nack(state, 1, delivery_tag, 0, 0);
            amqp_destroy_envelope(&envelope);

            if (nack_status != AMQP_STATUS_OK)
                throw std::runtime_error(
                    std::string("Could not dead-letter poison delivery: ") +
                    amqp_error_string2(nack_status));

            std::cerr << "Poison message rejected to DLQ: "
                      << exception.what() << '\n';
            std::cout << "Dead-letter NACK sent (requeue=false).\n";
            continue;
        } catch (...) {
            amqp_destroy_envelope(&envelope);
            throw; // Unknown/transient failures remain unacked and are requeued.
        }

        if (should_ack) {
            const int ack_status =
                amqp_basic_ack(state, 1, delivery_tag, 0);

            amqp_destroy_envelope(&envelope);

            if (ack_status != AMQP_STATUS_OK)
                throw std::runtime_error(
                    std::string("RabbitMQ ACK failed: ") +
                    amqp_error_string2(ack_status));

            std::cout << "ACK sent.\n";
        } else {
            amqp_destroy_envelope(&envelope);
            throw std::runtime_error(
                "Delivery was not acknowledged; stopping worker.");
        }
    }
}

}

int main(int argc, char** argv)
{
    const bool check_only =
        argc == 2 && std::string(argv[1]) == "--check-connection";
    const bool run =
        argc == 2 && std::string(argv[1]) == "--run";

    if (!check_only && !run) {
        std::cerr
            << "Usage:\n"
            << "  forge_cpp_worker --check-connection\n"
            << "  forge_cpp_worker --run\n"
            << "The --run mode consumes messages from the configured queue.\n";
        return EXIT_FAILURE;
    }

    try {
        const auto username = env_or("RABBITMQ_DEFAULT_USER", "");
        const auto password = env_or("RABBITMQ_DEFAULT_PASS", "");

        if (username.empty() || password.empty())
            throw std::runtime_error(
                "RabbitMQ username/password environment variables are required.");

        Connection connection(
            env_or("FORGE_RABBITMQ_HOST", "localhost"),
            port_from_environment(),
            env_or("FORGE_RABBITMQ_VHOST", "/"),
            username,
            password);

        const auto exchange =
            env_or("FORGE_RABBITMQ_EXCHANGE", "forge.jobs");
        const auto queue =
            env_or("FORGE_RABBITMQ_QUEUE", "forge.job-available.v1");
        const auto routing_key =
            env_or("FORGE_RABBITMQ_ROUTING_KEY", "job.available.v1");
        const auto dead_letter_exchange =
            env_or("FORGE_RABBITMQ_DEAD_LETTER_EXCHANGE", "");
        const auto dead_letter_queue =
            env_or("FORGE_RABBITMQ_DEAD_LETTER_QUEUE", "");
        const auto dead_letter_routing_key =
            env_or("FORGE_RABBITMQ_DEAD_LETTER_ROUTING_KEY", "");

        const bool any_dead_letter_setting =
            !dead_letter_exchange.empty() ||
            !dead_letter_queue.empty() ||
            !dead_letter_routing_key.empty();

        const bool all_dead_letter_settings =
            !dead_letter_exchange.empty() &&
            !dead_letter_queue.empty() &&
            !dead_letter_routing_key.empty();

        if (any_dead_letter_setting && !all_dead_letter_settings)
            throw std::runtime_error(
                "All three dead-letter environment variables are required together.");

        connection.ensure_topology(
            exchange, queue, routing_key,
            dead_letter_exchange, dead_letter_queue,
            dead_letter_routing_key);

        if (check_only) {
            std::cout
                << "PASS: Connected to RabbitMQ and verified topology.\n"
                << "No queue messages were consumed.\n";
            return EXIT_SUCCESS;
        }

        forge::worker::ApiClient api;
        run_consumer(connection, api, queue, all_dead_letter_settings);
        return EXIT_SUCCESS;
    } catch (const std::exception& exception) {
        std::cerr << "Worker stopped safely: " << exception.what() << '\n';
        return EXIT_FAILURE;
    }
}
